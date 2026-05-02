using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Player;
using DroneConsoleComponent = Content.Server.Shuttles.DroneConsoleComponent;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;
using Robust.Shared.Timing;

namespace Content.Server.Physics.Controllers;

public sealed class MoverController : SharedMoverController
{
    [Dependency] private readonly ThrusterSystem _thruster = default!;
    [Dependency] private readonly SharedTransformSystem _xformSystem = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;

    private Dictionary<EntityUid, (ShuttleComponent, List<(EntityUid, PilotComponent, InputMoverComponent, TransformComponent)>)> _shuttlePilots = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RelayInputMoverComponent, PlayerAttachedEvent>(OnRelayPlayerAttached);
        SubscribeLocalEvent<RelayInputMoverComponent, PlayerDetachedEvent>(OnRelayPlayerDetached);
        SubscribeLocalEvent<InputMoverComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<InputMoverComponent, PlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<PilotComponent, GetShuttleInputsEvent>(OnPilotGetInputs); // Mono

        SubscribeLocalEvent<PilotedShuttleComponent, StartCollideEvent>(PilotedShuttleRelayEvent<StartCollideEvent>); // Mono
    }

    private void OnRelayPlayerAttached(Entity<RelayInputMoverComponent> entity, ref PlayerAttachedEvent args)
    {
        if (MoverQuery.TryGetComponent(entity.Comp.RelayEntity, out var inputMover))
            SetMoveInput((entity.Comp.RelayEntity, inputMover), MoveButtons.None);
    }

    private void OnRelayPlayerDetached(Entity<RelayInputMoverComponent> entity, ref PlayerDetachedEvent args)
    {
        if (MoverQuery.TryGetComponent(entity.Comp.RelayEntity, out var inputMover))
            SetMoveInput((entity.Comp.RelayEntity, inputMover), MoveButtons.None);
    }

    private void OnPlayerAttached(Entity<InputMoverComponent> entity, ref PlayerAttachedEvent args)
    {
        SetMoveInput(entity, MoveButtons.None);
    }

    private void OnPlayerDetached(Entity<InputMoverComponent> entity, ref PlayerDetachedEvent args)
    {
        SetMoveInput(entity, MoveButtons.None);
    }

    private void OnPilotGetInputs(Entity<PilotComponent> entity, ref GetShuttleInputsEvent args)
    {
        var input = GetPilotVelocityInput(entity.Comp);
        args.GotInput = true;

        // don't slow down the ship if we're just looking at the console with zero input
        if (input.Brakes == 0f && input.Rotation == 0f && input.Strafe.LengthSquared() == 0f)
            return;

        args.Input = input;
    }

    private void PilotedShuttleRelayEvent<TEvent>(Entity<PilotedShuttleComponent> entity, ref TEvent args)
    {
        foreach (var pilot in entity.Comp.InputSources)
        {
            var relayEv = new PilotedShuttleRelayedEvent<TEvent>(args);
            RaiseLocalEvent(pilot, ref relayEv);
        }
    }

    protected override bool CanSound()
    {
        return true;
    }

    private HashSet<EntityUid> _moverAdded = new();
    private List<Entity<InputMoverComponent>> _movers = new();

    private void InsertMover(Entity<InputMoverComponent> source)
    {
        if (TryComp(source, out MovementRelayTargetComponent? relay))
        {
            if (TryComp(relay.Source, out InputMoverComponent? relayMover))
            {
                InsertMover((relay.Source, relayMover));
            }
        }

        // Already added
        if (!_moverAdded.Add(source.Owner))
            return;

        _movers.Add(source);
    }

    public override void UpdateBeforeSolve(bool prediction, float frameTime)
    {
        base.UpdateBeforeSolve(prediction, frameTime);

        _moverAdded.Clear();
        _movers.Clear();
        var inputQueryEnumerator = AllEntityQuery<InputMoverComponent>();

        // Need to order mob movement so that movers don't run before their relays.
        while (inputQueryEnumerator.MoveNext(out var uid, out var mover))
        {
            var physicsUid = uid;

            if (RelayQuery.HasComponent(uid))
                continue;

            if (!XformQuery.TryGetComponent(uid, out var xform))
            {
                continue;
            }

            // HardLight: guard against a stale parent chain. HandleMobMovement walks up the
            // parent chain via _transform.GetWorldRotation(xform), which throws KeyNotFoundException
            // if any ancestor's TransformComponent has been deleted (chaotic round shutdown,
            // grid cleanup races, etc.). The mover's own xform can survive its parent dying for
            // a tick before re-parenting catches up. Skip this mover for the tick instead of
            // spamming the runtime log every frame for every affected entity.
            if (xform.ParentUid != EntityUid.Invalid && !XformQuery.HasComponent(xform.ParentUid))
            {
                continue;
            }

            PhysicsComponent? body;
            var xformMover = xform;

            // Check if we should move the parent instead (for relays)
            if (RelayQuery.HasComponent(xform.ParentUid))
            {
                if (!PhysicsQuery.TryGetComponent(xform.ParentUid, out body) ||
                    !XformQuery.TryGetComponent(xform.ParentUid, out xformMover))
                {
                    continue;
                }

                physicsUid = xform.ParentUid;
            }
            else if (!PhysicsQuery.TryGetComponent(uid, out body))
            {
                continue;
            }

            // perf: if the physics body is asleep, no movement keys are held, and there is no
            // pending rotation lerp, HandleMobMovement has nothing to do this tick (no velocity
            // changes, no friction, no lerp progress). Any code that wakes the body earlier in
            // the same tick (damage, interactions, etc.) sets body.Awake = true before
            // UpdateBeforeSolve runs, so the guard won't fire for those cases.
            if (!body.Awake
                && mover.HeldMoveButtons == MoveButtons.None
                && mover.CurTickWalkMovement.LengthSquared() == 0f
                && mover.CurTickSprintMovement.LengthSquared() == 0f
                && mover.TargetRelativeRotation == mover.RelativeRotation)
                continue;

            HandleMobMovement((uid, mover), frameTime);
        }

        HandleShuttlePilot(frameTime);

        HandleShuttleMovement(frameTime);
    }

    // Mono: make ShuttleInput
    public ShuttleInput GetPilotVelocityInput(PilotComponent component)
    {
        if (!Timing.InSimulation)
        {
            // Outside of simulation we'll be running client predicted movement per-frame.
            // So return a full-length vector as if it's a full tick.
            // Physics system will have the correct time step anyways.
            ResetSubtick(component);
            ApplyTick(component, 1f);
            return new ShuttleInput(component.CurTickStrafeMovement, component.CurTickRotationMovement, component.CurTickBraking);
        }

        float remainingFraction;

        if (Timing.CurTick > component.LastInputTick)
        {
            component.CurTickStrafeMovement = Vector2.Zero;
            component.CurTickRotationMovement = 0f;
            component.CurTickBraking = 0f;
            remainingFraction = 1;
        }
        else
        {
            remainingFraction = (ushort.MaxValue - component.LastInputSubTick) / (float) ushort.MaxValue;
        }

        ApplyTick(component, remainingFraction);

        // Logger.Info($"{curDir}{walk}{sprint}");
        return new ShuttleInput(component.CurTickStrafeMovement, component.CurTickRotationMovement, component.CurTickBraking);
    }

    private void ResetSubtick(PilotComponent component)
    {
        if (Timing.CurTick <= component.LastInputTick) return;

        component.CurTickStrafeMovement = Vector2.Zero;
        component.CurTickRotationMovement = 0f;
        component.CurTickBraking = 0f;
        component.LastInputTick = Timing.CurTick;
        component.LastInputSubTick = 0;
    }

    protected override void HandleShuttleInput(EntityUid uid, ShuttleButtons button, ushort subTick, bool state)
    {
        if (!TryComp<PilotComponent>(uid, out var pilot) || pilot.Console == null)
            return;

        // WEP is a one-shot activation, not a held state
        if (button == ShuttleButtons.Wep && state)
        {
            var consoleXform = Transform(pilot.Console.Value);
            if (consoleXform.GridUid is { } gridUid && TryComp<ShuttleComponent>(gridUid, out var wepShuttle))
                ActivateWEP(gridUid, wepShuttle);
            return;
        }

        ResetSubtick(pilot);

        if (subTick >= pilot.LastInputSubTick)
        {
            var fraction = (subTick - pilot.LastInputSubTick) / (float) ushort.MaxValue;

            ApplyTick(pilot, fraction);
            pilot.LastInputSubTick = subTick;
        }

        var buttons = pilot.HeldButtons;

        if (state)
        {
            buttons |= button;
        }
        else
        {
            buttons &= ~button;
        }

        pilot.HeldButtons = buttons;
    }

    private static void ApplyTick(PilotComponent component, float fraction)
    {
        var x = 0;
        var y = 0;
        var rot = 0;
        int brake;

        if ((component.HeldButtons & ShuttleButtons.StrafeLeft) != 0x0)
        {
            x -= 1;
        }

        if ((component.HeldButtons & ShuttleButtons.StrafeRight) != 0x0)
        {
            x += 1;
        }

        component.CurTickStrafeMovement.X += x * fraction;

        if ((component.HeldButtons & ShuttleButtons.StrafeUp) != 0x0)
        {
            y += 1;
        }

        if ((component.HeldButtons & ShuttleButtons.StrafeDown) != 0x0)
        {
            y -= 1;
        }

        component.CurTickStrafeMovement.Y += y * fraction;

        if ((component.HeldButtons & ShuttleButtons.RotateLeft) != 0x0)
        {
            rot -= 1;
        }

        if ((component.HeldButtons & ShuttleButtons.RotateRight) != 0x0)
        {
            rot += 1;
        }

        component.CurTickRotationMovement += rot * fraction;

        if ((component.HeldButtons & ShuttleButtons.Brake) != 0x0)
        {
            brake = 1;
        }
        else
        {
            brake = 0;
        }

        component.CurTickBraking += brake * fraction;
    }

    #region mono
    //
    // Mono: all below code handling shuttle movement has been heavily modified by Monolith
    //

    /// <summary>
    /// Get a shuttle's angular acceleration.
    /// </summary>
    public float GetAngularAcceleration(ShuttleComponent shuttle, PhysicsComponent body)
    {
        return shuttle.AngularThrust * body.InvI;
    }

    /// <summary>
    /// Get shuttle thrust in a given direction.
    /// Takes local direction.
    /// </summary>
    public Vector2 GetDirectionThrust(Vector2 dir, ShuttleComponent shuttle, PhysicsComponent body)
    {
        if (dir.Length() == 0f)
            return Vector2.Zero;

        dir.Normalize();

        var horizIndex = dir.X > 0 ? 1 : 3; // east else west
        var vertIndex = dir.Y > 0 ? 2 : 0; // north else south
        var horizThrust = shuttle.LinearThrust[horizIndex];
        var vertThrust = shuttle.LinearThrust[vertIndex];

        var horizScale = MathF.Abs(horizThrust / dir.X);
        var vertScale = MathF.Abs(vertThrust / dir.Y);
        dir *= MathF.Min(horizScale, vertScale);

        return dir;
    }

    /// <summary>
    /// Activates WEP boost on a shuttle. Returns false if on cooldown.
    /// Speed is scaled by grid tile count: 250 tiles → 100 m/s, log2-linear, clamped 50–125.
    /// </summary>
    public bool ActivateWEP(EntityUid gridUid, ShuttleComponent shuttle)
    {
        if (_timing.CurTime < shuttle.WepCooldownExpiry)
            return false;

        // Compute WEP max velocity from grid tile count.
        var tileCount = ShuttleComponent.WepBaseGridSize;
        if (TryComp<MapGridComponent>(gridUid, out var mapGrid))
            tileCount = MathF.Max(1f, _mapSystem.GetAllTiles(gridUid, mapGrid).Count());

        var rawVel = ShuttleComponent.WepBaseVelocity - 25f * MathF.Log2(tileCount / ShuttleComponent.WepBaseGridSize);
        shuttle.WepBoostMaxVelocity = Math.Clamp(rawVel, ShuttleComponent.WepLowerVelocity, ShuttleComponent.WepUpperVelocity);

        shuttle.WepBoostActive = true;
        shuttle.WepBoostExpiry = _timing.CurTime + TimeSpan.FromSeconds(ShuttleComponent.WepBoostDuration);
        shuttle.WepBleedExpiry = shuttle.WepBoostExpiry + TimeSpan.FromSeconds(ShuttleComponent.WepBleedDuration);
        shuttle.WepCooldownExpiry = shuttle.WepBoostExpiry + TimeSpan.FromSeconds(ShuttleComponent.WepCooldownDuration);
        shuttle.WepThrustMultiplier = shuttle.WepBoostMaxVelocity / ShuttleComponent.WepLowerVelocity;

        // Play looping WEP audio on the grid.
        shuttle.WepAudioStream = _audio.Stop(shuttle.WepAudioStream);
        var stream = _audio.PlayPvs(new SoundPathSpecifier("/Audio/_HL/Effects/wep_buzz.ogg")
            { Params = AudioParams.Default.WithLoop(true).WithVolume(-3f) }, gridUid);
        shuttle.WepAudioStream = stream?.Entity;
        _audio.SetGridAudio(stream);

        EntityManager.System<ShuttleConsoleSystem>().OnWEPActivated(gridUid);
        return true;
    }

    public Vector2 ObtainMaxVel(Vector2 vel, ShuttleComponent shuttle, PhysicsComponent body) // mono
    {
        vel.Normalize(); // Vector2 is a struct so this acts on a copy
        var maxVel = (shuttle.WepBoostActive && _timing.CurTime < shuttle.WepBoostExpiry)
            ? shuttle.WepBoostMaxVelocity
            : shuttle.BaseMaxLinearVelocity;
        return vel * maxVel;
    }

    private void HandleShuttleMovement(float frameTime)
    {
        var shuttleQuery = EntityQueryEnumerator<ShuttleComponent, PilotedShuttleComponent, PhysicsComponent>();
        while (shuttleQuery.MoveNext(out var uid, out var shuttle, out var piloted, out var body))
        {
            var inputs = new List<ShuttleInput>();
            // query all our pilots for input
            var toRemove = new List<EntityUid>();

            foreach (var pilot in piloted.InputSources)
            {
                var inputsEv = new GetShuttleInputsEvent(frameTime);
                RaiseLocalEvent(pilot, ref inputsEv);

                if (!inputsEv.GotInput)
                    toRemove.Add(pilot);
                else if (inputsEv.Input != null)
                    inputs.Add(inputsEv.Input.Value);
            }

            foreach (var remUid in toRemove)
            {
                piloted.InputSources.Remove(remUid);
            }

            var count = inputs.Count;

            // HL: WEP bleed - decelerate to normal max speed over 1 second after WEP expires
            if (!shuttle.WepBoostActive && _timing.CurTime < shuttle.WepBleedExpiry)
            {
                var speed = body.LinearVelocity.Length();
                if (speed > shuttle.BaseMaxLinearVelocity)
                {
                    PhysicsSystem.SetSleepingAllowed(uid, body, false);
                    var bleedTimeRemaining = (float)(shuttle.WepBleedExpiry - _timing.CurTime).TotalSeconds;
                    var excessSpeed = speed - shuttle.BaseMaxLinearVelocity;
                    var dv = MathF.Min(excessSpeed * frameTime / bleedTimeRemaining, excessSpeed);
                    var brakeForce = -body.LinearVelocity.Normalized() * dv * body.Mass / frameTime;
                    PhysicsSystem.ApplyForce(uid, brakeForce, body: body);
                }
            }
            // End HL

            if (count == 0)
            {
                _thruster.DisableLinearThrusters(shuttle);
                PhysicsSystem.SetSleepingAllowed(uid, body, true);
                continue;
            }
            PhysicsSystem.SetSleepingAllowed(uid, body, false);

            // get the averaged input from all controllers
            var linearInput = Vector2.Zero;
            var angularInput = 0f;
            var brakeInput = 0f;
            foreach (var inp in inputs)
            {
                linearInput += inp.Strafe.LengthSquared() > 1 ? inp.Strafe.Normalized() : inp.Strafe;
                angularInput += MathHelper.Clamp(inp.Rotation, -1f, 1f);
                brakeInput += MathF.Min(inp.Brakes, 1f);
            }
            linearInput /= count;
            angularInput /= count;
            brakeInput /= count;

            var shuttleNorthAngle = _xformSystem.GetWorldRotation(uid);

            // handle movement: brake
            if (brakeInput > 0f)
            {
                if (body.LinearVelocity.Length() > 0f)
                {
                    // Minimum brake velocity for a direction to show its thrust appearance.
                    const float appearanceThreshold = 0.1f;

                    // Get velocity relative to the shuttle so we know which thrusters to fire
                    var shuttleVelocity = (-shuttleNorthAngle).RotateVec(body.LinearVelocity);
                    var force = GetDirectionThrust(-shuttleVelocity, shuttle, body);

                    if (force.X < 0f)
                    {
                        _thruster.DisableLinearThrustDirection(shuttle, DirectionFlag.West);
                        if (shuttleVelocity.X < -appearanceThreshold)
                            _thruster.EnableLinearThrustDirection(shuttle, DirectionFlag.East);
                    }
                    else if (force.X > 0f)
                    {
                        _thruster.DisableLinearThrustDirection(shuttle, DirectionFlag.East);
                        if (shuttleVelocity.X > appearanceThreshold)
                            _thruster.EnableLinearThrustDirection(shuttle, DirectionFlag.West);
                    }

                    if (shuttleVelocity.Y < 0f)
                    {
                        _thruster.DisableLinearThrustDirection(shuttle, DirectionFlag.South);
                        if (shuttleVelocity.Y < -appearanceThreshold)
                            _thruster.EnableLinearThrustDirection(shuttle, DirectionFlag.North);
                    }
                    else if (shuttleVelocity.Y > 0f)
                    {
                        _thruster.DisableLinearThrustDirection(shuttle, DirectionFlag.North);
                        if (shuttleVelocity.Y > appearanceThreshold)
                            _thruster.EnableLinearThrustDirection(shuttle, DirectionFlag.South);

                    }

                    var impulse = force * brakeInput * ShuttleComponent.BrakeCoefficient;
                    impulse = shuttleNorthAngle.RotateVec(impulse);
                    var maxForce = body.LinearVelocity.Length() * body.Mass / frameTime;

                    if (maxForce == 0f)
                        impulse = Vector2.Zero;
                    // Don't overshoot
                    else if (impulse.Length() > maxForce)
                        impulse = impulse.Normalized() * maxForce;

                    PhysicsSystem.ApplyForce(uid, impulse, body: body);
                }
                else
                {
                    _thruster.DisableLinearThrusters(shuttle);
                }

                if (body.AngularVelocity != 0f)
                {
                    var torque = shuttle.AngularThrust * brakeInput * (body.AngularVelocity > 0f ? -1f : 1f) * ShuttleComponent.BrakeCoefficient;
                    var torqueMul = body.InvI * frameTime;

                    if (body.AngularVelocity > 0f)
                    {
                        torque = MathF.Max(-body.AngularVelocity / torqueMul, torque);
                    }
                    else
                    {
                        torque = MathF.Min(-body.AngularVelocity / torqueMul, torque);
                    }

                    if (!torque.Equals(0f))
                    {
                        PhysicsSystem.ApplyTorque(uid, torque, body: body);
                        _thruster.SetAngularThrust(shuttle, true);
                    }
                }
                else
                {
                    _thruster.SetAngularThrust(shuttle, false);
                }
            }

            if (linearInput.Length().Equals(0f))
            {
                if (brakeInput.Equals(0f))
                    _thruster.DisableLinearThrusters(shuttle);
            }
            else
            {
                var angle = linearInput.ToWorldAngle();
                var linearDir = angle.GetDir();
                var dockFlag = linearDir.AsFlag();

                var totalForce = GetDirectionThrust(linearInput, shuttle, body);

                // Won't just do cardinal directions.
                foreach (DirectionFlag dir in Enum.GetValues(typeof(DirectionFlag)))
                {
                    // Brain no worky but I just want cardinals
                    switch (dir)
                    {
                        case DirectionFlag.South:
                        case DirectionFlag.East:
                        case DirectionFlag.North:
                        case DirectionFlag.West:
                            break;
                        default:
                            continue;
                    }

                    if ((dir & dockFlag) == 0x0)
                        _thruster.DisableLinearThrustDirection(shuttle, dir);
                    else
                        _thruster.EnableLinearThrustDirection(shuttle, dir);
                }

                var localVel = (-shuttleNorthAngle).RotateVec(body.LinearVelocity);
                // vector of max velocity we can be traveling with along current direction
                var maxVelocity = ObtainMaxVel(localVel, shuttle, body);
                // vector of max velocity we can be traveling with along wish-direction
                var maxWishVelocity = ObtainMaxVel(totalForce, shuttle, body);
                // if we're going faster than we can be, thrust to adjust our velocity to the max wish-direction velocity
                if (localVel.LengthSquared() > maxVelocity.LengthSquared())
                {
                    var velDelta = maxWishVelocity - maxVelocity;
                    var maxForceLength = velDelta.Length() * body.Mass / frameTime;
                    var appliedLength = MathF.Min(totalForce.Length(), maxForceLength);
                    totalForce = velDelta.Length() == 0 ? Vector2.Zero : velDelta.Normalized() * appliedLength;
                }

                // HL: WEP thrust boost (pre-computed multiplier, no extra per-tick work)
                if (shuttle.WepBoostActive)
                    totalForce *= shuttle.WepThrustMultiplier;

                totalForce = shuttleNorthAngle.RotateVec(totalForce);

                if (totalForce.Length() > 0f)
                    PhysicsSystem.ApplyForce(uid, totalForce, body: body);
            }

            if (MathHelper.CloseTo(angularInput, 0f))
            {
                if (brakeInput <= 0f)
                    _thruster.SetAngularThrust(shuttle, false);
            }
            else
            {
                var torque = shuttle.AngularThrust * -angularInput;

                // Need to cap the velocity if 1 tick of input brings us over cap so we don't continuously
                // edge onto the cap over and over.
                var torqueMul = body.InvI * frameTime;

                torque = Math.Clamp(torque,
                    (-ShuttleComponent.MaxAngularVelocity - body.AngularVelocity) / torqueMul,
                    (ShuttleComponent.MaxAngularVelocity - body.AngularVelocity) / torqueMul);

                if (!torque.Equals(0f))
                {
                    PhysicsSystem.ApplyTorque(uid, torque, body: body);
                    _thruster.SetAngularThrust(shuttle, true);
                }
            }
        }
    }

    private void HandleShuttlePilot(float frameTime)
    {
        var newPilots = new Dictionary<EntityUid, (ShuttleComponent Shuttle, List<(EntityUid PilotUid, PilotComponent Pilot, InputMoverComponent Mover, TransformComponent ConsoleXform)>)>();

        // We just mark off their movement and the shuttle itself does its own movement
        var activePilotQuery = EntityQueryEnumerator<PilotComponent, InputMoverComponent>();
        var shuttleQuery = GetEntityQuery<ShuttleComponent>();
        while (activePilotQuery.MoveNext(out var uid, out var pilot, out var mover))
        {
            var consoleEnt = pilot.Console;

            // TODO: This is terrible. Just make a new mover and also make it remote piloting + device networks
            if (TryComp<DroneConsoleComponent>(consoleEnt, out var cargoConsole))
            {
                consoleEnt = cargoConsole.Entity;
            }

            if (!TryComp(consoleEnt, out TransformComponent? xform)) continue;

            var gridId = xform.GridUid;
            // This tries to see if the grid is a shuttle and if the console should work.
            if (!TryComp<MapGridComponent>(gridId, out var _) ||
                !shuttleQuery.TryGetComponent(gridId, out var shuttleComponent) ||
                !shuttleComponent.Enabled)
                continue;

            if (!newPilots.TryGetValue(gridId!.Value, out var pilots))
            {
                pilots = (shuttleComponent, new List<(EntityUid, PilotComponent, InputMoverComponent, TransformComponent)>());
                newPilots[gridId.Value] = pilots;
            }

            pilots.Item2.Add((uid, pilot, mover, xform));
        }

        _shuttlePilots = newPilots;


        // Collate all of the linear / angular velocites for a shuttle
        // then do the movement input once for it.
        foreach (var (shuttleUid, (shuttle, pilots)) in _shuttlePilots)
        {
            if (Paused(shuttleUid) || CanPilot(shuttleUid) || !TryComp<PhysicsComponent>(shuttleUid, out var body))
                continue;

            foreach (var (pilotUid, _, _, _) in pilots)
            {
                AddPilot(shuttleUid, pilotUid);
            }
        }
    }

    /// <summary>
    /// Registers an entity as an input source for a shuttle.
    /// </summary>
    public void AddPilot(EntityUid shuttleUid, EntityUid pilot)
    {
        var shuttle = EnsureComp<PilotedShuttleComponent>(shuttleUid);
        shuttle.InputSources.Add(pilot);
    }

    #endregion

    // .NET 8 seem to miscompile usage of Vector2.Dot above. This manual outline fixes it pending an upstream fix.
    // See PR #24008
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float Vector2Dot(Vector2 value1, Vector2 value2)
    {
        return Vector2.Dot(value1, value2);
    }

    private bool CanPilot(EntityUid shuttleUid)
    {
        return TryComp<FTLComponent>(shuttleUid, out var ftl)
        && (ftl.State & (FTLState.Starting | FTLState.Travelling | FTLState.Arriving)) != 0x0
            || HasComp<PreventPilotComponent>(shuttleUid);
    }

}


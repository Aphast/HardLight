using Content.Shared.Actions;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Shared.Weapons.Ranged.Systems;

public sealed class ActionGunSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ActionGunComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ActionGunComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ActionGunComponent, ActionGunShootEvent>(OnShoot);
    }

    private void OnMapInit(Entity<ActionGunComponent> ent, ref MapInitEvent args)
    {
        if (string.IsNullOrEmpty(ent.Comp.Action))
            return;

        _actions.AddAction(ent, ref ent.Comp.ActionEntity, ent.Comp.Action);
        // Spawn the gun at the owner's coordinates so projectiles originate correctly
        // and parent it to the owner so it moves with them.
        ent.Comp.Gun = Spawn(ent.Comp.GunProto, Transform(ent).Coordinates);
        if (ent.Comp.Gun != null)
            _transform.SetParent(ent.Comp.Gun.Value, ent);

        //Log.Info($"ActionGun MapInit: entity={ent.Owner} actionProto={ent.Comp.Action} actionEntity={ent.Comp.ActionEntity} gunProto={ent.Comp.GunProto} spawnedGun={ent.Comp.Gun}");
    }

    private void OnShutdown(Entity<ActionGunComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Gun != null)
            QueueDel(ent.Comp.Gun.Value);
    }

    private void OnShoot(Entity<ActionGunComponent> ent, ref ActionGunShootEvent args)
    {
       //Log.Info($"ActionGun OnShoot: entity={ent.Owner} gun={ent.Comp.Gun} target={args.Target}");
        // Ensure we have a spawned gun. MapInit may not have run for entities spawned at runtime,
        // so spawn lazily here if needed.
        if (ent.Comp.Gun == null || !Exists(ent.Comp.Gun.Value))
        {
            if (string.IsNullOrEmpty(ent.Comp.GunProto))
            {
                //Log.Info($"ActionGun OnShoot: no GunProto defined for {ent.Owner}, cannot spawn gun");
                return;
            }

            var spawned = Spawn(ent.Comp.GunProto, Transform(ent).Coordinates);
            ent.Comp.Gun = spawned;
            _transform.SetParent(spawned, ent);
            //Log.Info($"ActionGun OnShoot: spawned missing gun {spawned} for {ent.Owner} from proto {ent.Comp.GunProto}");
        }

        if (!TryComp<GunComponent>(ent.Comp.Gun, out var gun))
        {
            //Log.Info($"ActionGun OnShoot: no GunComponent found on {ent.Comp.Gun}");
            return;
        }

        _gun.AttemptShoot(ent, ent.Comp.Gun.Value, gun, args.Target);
    }
}


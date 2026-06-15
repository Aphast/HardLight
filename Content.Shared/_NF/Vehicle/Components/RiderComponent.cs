// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.

using Content.Shared.Vehicle;
using Robust.Shared.GameStates;

namespace Content.Shared._NF.Vehicle.Components;

/// <summary>
/// Denotes an entity as being in control of a vehicle.
/// </summary>
[RegisterComponent, NetworkedComponent, Access(typeof(SharedVehicleSystem))]
public sealed partial class VehicleRiderComponent : Component;

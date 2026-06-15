// SPDX-FileCopyrightText: 2025 deltanedas <@deltanedas:kde.org>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;

namespace Content.Shared._Goobstation.MartialArts.Events;

/// <summary>
/// Event raised when a virtual item is thrown (grab intent mechanic).
/// </summary>
[Serializable, NetSerializable]
public sealed partial class VirtualItemThrownEvent : EntityEventArgs
{
    public Angle Direction { get; set; }
}

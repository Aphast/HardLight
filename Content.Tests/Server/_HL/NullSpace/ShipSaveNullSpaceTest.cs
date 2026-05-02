using System;
using Content.Server._HL.Shipyard;
using NUnit.Framework;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Sequence;
using Robust.Shared.Serialization.Markdown.Value;
#nullable enable

namespace Content.Tests.Server._HL.NullSpace;

/// <summary>
/// Tests that NullSpace items are stripped from ship saves by <see cref="ShipSaveYamlSanitizer"/>.
/// These act as regression guards: if a prototype is renamed or the sanitizer list is edited,
/// the test will fail and prompt the author to update both sides together.
/// </summary>
[TestFixture, TestOf(typeof(ShipSaveYamlSanitizer))]
[Parallelizable(ParallelScope.All)]
public sealed class ShipSaveNullSpaceTest
{
    /// <summary>
    /// Builds a minimal ship-save root node containing a single entity of the given prototype ID.
    /// </summary>
    private static MappingDataNode BuildSaveWithProto(string protoId)
    {
        var entity = new MappingDataNode();
        entity["uid"] = new ValueDataNode("1");
        var comps = new SequenceDataNode();
        var comp = new MappingDataNode();
        comp["type"] = new ValueDataNode("Sprite");
        comps.Add(comp);
        entity["components"] = comps;

        var entityList = new SequenceDataNode();
        entityList.Add(entity);

        var protoGroup = new MappingDataNode();
        protoGroup["proto"] = new ValueDataNode(protoId);
        protoGroup["entities"] = entityList;

        var protoSeq = new SequenceDataNode();
        protoSeq.Add(protoGroup);

        var root = new MappingDataNode();
        root["entities"] = protoSeq;
        return root;
    }

    /// <summary>
    /// Counts surviving entity instances in the named proto group after sanitization.
    /// </summary>
    private static int CountEntitiesInProtoGroup(MappingDataNode root, string protoId)
    {
        if (!root.TryGet("entities", out SequenceDataNode? protoSeq) || protoSeq == null)
            return 0;

        foreach (var node in protoSeq)
        {
            if (node is not MappingDataNode protoMap) continue;
            if (!protoMap.TryGet("proto", out ValueDataNode? idNode) || idNode == null) continue;
            if (!string.Equals(idNode.Value, protoId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!protoMap.TryGet("entities", out SequenceDataNode? entities) || entities == null) return 0;
            return entities.Count;
        }

        return 0;
    }

    [Test]
    [TestCase("ClothingEyesGlassesNullSpace", TestName = "NullSpaceGogglesStripped")]
    [TestCase("BluespaceFlasher",             TestName = "BluespaceFlasherStripped")]
    [TestCase("ClothingNullHarness",          TestName = "NullHarnessStripped")]
    [TestCase("ClothingNullSpaceTeleporter",  TestName = "NullSpaceTeleporterStripped")]
    [TestCase("BluespaceAnchor",              TestName = "BluespaceAnchorStripped")]
    public void NullSpaceItemRemovedFromShipSave(string protoId)
    {
        var root = BuildSaveWithProto(protoId);

        // Filtered prototypes are short-circuited before prototypeManager.TryIndex is ever reached,
        // so null is safe here for these specific IDs.
        ShipSaveYamlSanitizer.SanitizeShipSaveNode(root, null!);

        Assert.That(CountEntitiesInProtoGroup(root, protoId), Is.Zero,
            $"Entity with prototype '{protoId}' should have been stripped from the ship save.");
    }

    [Test]
    public void NonFilteredEntitySurvivesShipSave()
    {
        // Build a proto group with no "proto" key (entity with no prototype, e.g. a map tile entity).
        // These should survive sanitization unchanged.
        var entity = new MappingDataNode();
        entity["uid"] = new ValueDataNode("99");
        var comps = new SequenceDataNode();
        var spriteComp = new MappingDataNode();
        spriteComp["type"] = new ValueDataNode("Sprite");
        comps.Add(spriteComp);
        entity["components"] = comps;

        var entityList = new SequenceDataNode();
        entityList.Add(entity);

        var protoGroup = new MappingDataNode(); // intentionally no "proto" key
        protoGroup["entities"] = entityList;

        var protoSeq = new SequenceDataNode();
        protoSeq.Add(protoGroup);

        var root = new MappingDataNode();
        root["entities"] = protoSeq;

        ShipSaveYamlSanitizer.SanitizeShipSaveNode(root, null!);

        // The entity list inside the group should still contain our entity.
        Assert.That(protoGroup.TryGet("entities", out SequenceDataNode? remaining) && remaining!.Count == 1,
            "An entity without a filtered prototype should survive ship-save sanitization.");
    }
}

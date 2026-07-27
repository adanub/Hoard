using Hoard.Core.Domain;
using Hoard.Core.Sync;
using Xunit;

namespace Hoard.Core.Tests;

/// <summary>
/// Delta replication derives "which images does the remote still need" from op payloads alone, so the
/// rule "an op that implies a stored blob names it as payload.relativePath" is load-bearing: an op kind
/// that spelled the field differently would leave its image out of a backup silently.
/// </summary>
public class ArchiveOpBlobsTests
{
    [Fact]
    public void An_added_op_names_its_blob_and_its_size()
    {
        var op = Op(ArchiveOpKinds.AssetAdded, ArchiveOpJson.Serialize(new AssetAddedPayload(
            "aa/bb/pin.jpg", "image/jpeg", MediaKind.Image, 10, 10, 4096,
            "pinterest", "pin-1", null, null, "Item", null, null,
            null, DateTimeOffset.UnixEpoch, null)));

        Assert.Equal(new ArchiveOpBlobs.Reference("aa/bb/pin.jpg", 4096), ArchiveOpBlobs.Referenced(op));
    }

    [Fact]
    public void A_refetched_op_names_the_blob_it_moved_to()
    {
        var op = Op(ArchiveOpKinds.AssetRefetched, ArchiveOpJson.Serialize(
            new AssetContentChangedPayload(new string('a', 64), "cc/dd/new.jpg", 77)));

        Assert.Equal(new ArchiveOpBlobs.Reference("cc/dd/new.jpg", 77), ArchiveOpBlobs.Referenced(op));
    }

    [Fact]
    public void Ops_that_imply_no_blob_name_none()
    {
        Assert.Null(ArchiveOpBlobs.Referenced(Op(ArchiveOpKinds.AssetTombstoned, ArchiveOpJson.Serialize(
            new AssetTombstonedPayload("gone", DateTimeOffset.UnixEpoch)))));
        Assert.Null(ArchiveOpBlobs.Referenced(Op(ArchiveOpKinds.ItemLinked, ArchiveOpJson.Serialize(
            new ItemLinkedPayload(null, null, DateTimeOffset.UnixEpoch)))));
        Assert.Null(ArchiveOpBlobs.Referenced(Op(ArchiveOpKinds.AssetRemoved, null)));
        Assert.Null(ArchiveOpBlobs.Referenced(Op(ArchiveOpKinds.AssetAdded, "{not json"))); // never throws
    }

    private static ArchiveOp Op(string kind, string? payload) => new()
    {
        DeviceId = "dev",
        Seq = 1,
        Hlc = "00000000000001-000000-dev",
        Kind = kind,
        PayloadJson = payload,
    };
}

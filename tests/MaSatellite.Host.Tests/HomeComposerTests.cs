using MaSatellite.Host.Presentation;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Tests;

public class HomeComposerTests
{
    private static MaItem Playlist(string name, bool playable = true) =>
        new(MaMediaType.Playlist, name, "library", name, "library://playlist/" + name, null, null, null, null, null, playable);

    [Fact]
    public void Time_pick_and_top_playlist_become_the_two_featured_cards()
    {
        var layout = HomeComposer.Compose([Playlist("A"), Playlist("B")], true, [Playlist("Morning")], [Playlist("New")]);
        Assert.Equal(FeaturedKind.ForThisMoment, layout.FirstFeatured!.Kind);
        Assert.Equal("Morning", layout.FirstFeatured.Item.Name);
        Assert.Equal(FeaturedKind.YourRotation, layout.SecondFeatured!.Kind);
        Assert.Equal("A", layout.SecondFeatured.Item.Name);
    }

    [Fact]
    public void Without_a_time_pick_the_newest_playlist_is_featured_honestly()
    {
        var layout = HomeComposer.Compose([Playlist("A")], true, [], [Playlist("New")]);
        Assert.Equal(FeaturedKind.RecentlyAdded, layout.FirstFeatured!.Kind);
    }

    [Fact]
    public void Unranked_playlists_do_not_claim_to_be_your_rotation()
    {
        var layout = HomeComposer.Compose([Playlist("A")], false, [], []);
        Assert.Equal(FeaturedKind.FromYourPlaylists, layout.SecondFeatured!.Kind);
    }

    [Fact]
    public void No_playlist_appears_twice_across_sections()
    {
        var a = Playlist("A");
        var b = Playlist("B");
        var layout = HomeComposer.Compose([a, b], true, [a], [a, b, Playlist("C")]);
        var all = layout.Featured.Select(f => f.Item.Uri)
            .Concat(layout.Rotation.Select(i => i.Uri))
            .Concat(layout.Added.Select(i => i.Uri)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(["library://playlist/C"], layout.Added.Select(i => i.Uri));
    }

    [Fact]
    public void Unplayable_or_uri_less_items_are_never_featured()
    {
        var layout = HomeComposer.Compose([Playlist("X", playable: false)], true, [], []);
        Assert.Null(layout.FirstFeatured);
        Assert.Null(layout.SecondFeatured);
        Assert.Empty(layout.Rotation);
    }

    [Fact]
    public void Empty_library_yields_an_empty_layout()
    {
        var layout = HomeComposer.Compose([], false, [], []);
        Assert.Empty(layout.Featured);
        Assert.Empty(layout.Added);
    }
}

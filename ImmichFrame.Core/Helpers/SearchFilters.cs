using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Helpers;

/// <summary>
/// An account's settings as the filter of Immich's search. The random draw and the statistics count
/// are both given what <see cref="ForAccount"/> builds, so that what an account counts cannot drift
/// from what it draws.
/// </summary>
public static class SearchFilters
{
    /// <summary>
    /// Leaves trashed assets out, by asking for the ones whose <c>trashedAt</c> is null. The generated
    /// filter cannot ask that: it leaves a null <c>eq</c> out of the request altogether.
    /// </summary>
    public static NullableDateFilter NotTrashed() => new() { Eq = null };

    public static EnumFilterAssetVisibility Visibility(IAccountSettings settings)
    {
        var visibilities = new List<AssetVisibility> { AssetVisibility.Timeline };
        if (settings.ShowArchived)
        {
            visibilities.Add(AssetVisibility.Archive);
        }

        return new EnumFilterAssetVisibility { In = visibilities };
    }

    // Left out when there is nothing to exclude: Immich refuses an empty list of ids.
    public static IdsFilter? ExcludedAlbums(IAccountSettings settings)
    {
        if (settings.ExcludedAlbums is not { Count: > 0 } excluded)
        {
            return null;
        }

        return new IdsFilter { None = excluded };
    }

    public static EnumFilterAssetType Types(IAccountSettings settings)
    {
        var types = new List<AssetTypeEnum> { AssetTypeEnum.IMAGE };
        if (settings.ShowVideos)
        {
            types.Add(AssetTypeEnum.VIDEO);
        }

        return new EnumFilterAssetType { In = types };
    }

    public static bool HasConfiguredSource(IAccountSettings settings) =>
        settings.ShowFavorites
        || settings.Albums is { Count: > 0 }
        || settings.People is { Count: > 0 }
        || settings.Tags is { Count: > 0 };

    /// <summary>
    /// Everything the account shows, as one filter: its own settings, and a branch of an "or" for each
    /// source it narrows to - its favourites, albums, people and tags - so that an asset from any one
    /// of them is shown once. An account narrowed to none has no "or" at all, and shows its library.
    /// </summary>
    public static SearchFilter ForAccount(IAccountSettings settings, IReadOnlyCollection<Guid> tagIds)
    {
        var filter = new SearchFilter
        {
            TrashedAt = NotTrashed(),
            Visibility = Visibility(settings),
            Type = Types(settings),
            AlbumIds = ExcludedAlbums(settings),
            TakenAt = TakenAt(settings),
            Rating = Rating(settings)
        };

        // Only when there is a branch: Immich refuses an empty "or", as it does an empty list of ids.
        var branches = Branches(settings, tagIds);
        if (branches.Count > 0)
        {
            filter.Or = branches;
        }

        return filter;
    }

    public static bool HasMatchingBranch(IAccountSettings settings, IReadOnlyCollection<Guid> tagIds) =>
        Branches(settings, tagIds).Count > 0;

    private static List<SearchFilterBranch> Branches(IAccountSettings settings, IReadOnlyCollection<Guid> tagIds)
    {
        var branches = new List<SearchFilterBranch>();
        if (settings.ShowFavorites)
        {
            branches.Add(new SearchFilterBranch { IsFavorite = new BoolFilter { Eq = true } });
        }

        if (settings.Albums is { Count: > 0 } albums)
        {
            branches.Add(new SearchFilterBranch { AlbumIds = new IdsFilter { Any = albums } });
        }

        if (settings.People is { Count: > 0 } people)
        {
            branches.Add(new SearchFilterBranch { PersonIds = new IdsFilter { Any = people } });
        }

        if (tagIds.Count > 0)
        {
            branches.Add(new SearchFilterBranch { TagIds = new IdsFilter { Any = tagIds.ToList() } });
        }

        return branches;
    }

    /// <summary>
    /// Both ends inclusive, as ImmichFrame has always read the account's dates, and still reads them
    /// for the memories it filters itself: a photo taken at the very moment ImagesFromDate or
    /// ImagesUntilDate names is shown. That moment is often midnight, which is the time a scanned
    /// photo dated only by its day is given.
    /// </summary>
    private static DateFilter? TakenAt(IAccountSettings settings)
    {
        var from = settings.ImagesFromDate
            ?? (settings.ImagesFromDays is int days ? DateTime.Today.AddDays(-days) : null);
        var until = settings.ImagesUntilDate;

        return from == null && until == null ? null : new DateFilter { Gte = from, Lte = until };
    }

    // At least the rating, rather than exactly it: an account set to 3 stars shows its 4 and 5 too.
    private static NumberFilterNullable? Rating(IAccountSettings settings) =>
        settings.Rating is int rating ? new NumberFilterNullable { Gte = rating } : null;
}

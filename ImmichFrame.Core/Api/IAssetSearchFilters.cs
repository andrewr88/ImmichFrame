namespace ImmichFrame.Core.Api
{
    /// <summary>
    /// The fields an account's settings filter a search by, as the random search and the statistics
    /// search both carry them. Given to both, so that one builder can fill in either and what
    /// <c>AllAssetsPool</c> counts cannot drift from what it draws from. Implemented by the generated
    /// properties themselves, so it adds no members of its own.
    /// </summary>
    internal interface IAssetSearchFilters
    {
        AssetTypeEnum? Type { get; set; }
        AssetVisibility? Visibility { get; set; }
        DateTimeOffset? TakenAfter { get; set; }
        DateTimeOffset? TakenBefore { get; set; }
        int? Rating { get; set; }
    }

    public partial class RandomSearchDto : IAssetSearchFilters
    {
    }

    public partial class StatisticsSearchDto : IAssetSearchFilters
    {
    }
}

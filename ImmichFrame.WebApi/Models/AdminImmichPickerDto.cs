namespace ImmichFrame.WebApi.Models;

/// <summary>
/// Which Immich account a picker request is to be answered from. Exactly one of the two ways of
/// naming one has to be filled in.
/// <para>
/// <strong>Saved</strong> - <paramref name="AccountId"/>, the opaque handle
/// <c>GET /api/admin/config</c> issued for that account, alongside the <paramref name="Version"/>
/// and <paramref name="Profile"/> it was read under. Nothing about the account travels here: the
/// handle is resolved against the configuration on the server, so a stored API key is never sent to
/// the browser and never sent back.
/// </para>
/// <para>
/// <strong>Inline</strong> - <paramref name="ServerUrl"/> and <paramref name="ApiKey"/> for an
/// account the administrator is still typing. This is what makes the picker useful during first-run
/// setup, when there is nothing saved to resolve and raw GUIDs hurt most.
/// </para>
/// </summary>
/// <param name="Profile">
/// The configuration the handle was issued under - a profile name, or <c>default</c> (or nothing)
/// for the default configuration. Note this is <em>not</em> the frame's <c>?profile=</c> parameter:
/// admin endpoints serve no configuration profile of their own, they administer all of them.
/// </param>
/// <param name="AccountId">
/// <see cref="AdminAccountSettingsDto.Id"/> as the configuration read handed it out.
/// </param>
/// <param name="Version">
/// The <see cref="AdminConfigDto.Version"/> that read reported. Checked before the handle is looked
/// up, so a configuration that changed underneath the editor is reported as exactly that rather
/// than as an account that has gone missing.
/// </param>
/// <param name="ServerUrl">An Immich base URL, for an account that has not been saved yet.</param>
/// <param name="ApiKey">Its API key. Used for this one request and never stored.</param>
public record AdminImmichAccountRefDto(
    string? Profile = null,
    string? AccountId = null,
    string? Version = null,
    string? ServerUrl = null,
    string? ApiKey = null);

/// <summary>
/// One Immich album, trimmed to what a picker draws. <see cref="Id"/> is what
/// <c>ServerAccountSettings.Albums</c> and <c>ExcludedAlbums</c> hold.
/// </summary>
public record AdminImmichAlbumDto(Guid Id, string? AlbumName, long AssetCount);

/// <summary>
/// One Immich person, trimmed to what a picker draws. <see cref="Id"/> is what
/// <c>ServerAccountSettings.People</c> holds.
/// <para>
/// <see cref="Name"/> is very often empty: Immich creates a person for every face cluster it finds
/// and only the ones somebody has labelled have a name. A picker has to render an unnamed person
/// sensibly - by its thumbnail, not by a blank row.
/// </para>
/// </summary>
public record AdminImmichPersonDto(Guid Id, string? Name);

/// <summary>
/// A page-through of every person on an Immich server, with the truncation stated rather than
/// implied.
/// </summary>
/// <param name="People">The people read, in the order Immich returned them.</param>
/// <param name="Total">How many people Immich says it has, whether or not they were all read.</param>
/// <param name="Truncated">
/// True when reading stopped at ImmichFrame's cap with pages still to go. An administrator who
/// cannot find a person needs to know the list was cut short, rather than conclude the person is
/// not there.
/// </param>
public record AdminImmichPeopleDto(IReadOnlyList<AdminImmichPersonDto> People, long Total, bool Truncated);

/// <summary>
/// One Immich tag, trimmed to what a picker draws.
/// <para>
/// <see cref="Value"/> - not <see cref="Id"/>, and not <see cref="Name"/> - is what
/// <c>ServerAccountSettings.Tags</c> holds: <c>TagAssetsPool</c> keys every tag by
/// <c>TagResponseDto.Value</c> and looks each configured string up in that dictionary. A picker that
/// submitted the id or the name would write a configuration that loads, validates, and then selects
/// no assets at all - a failure with nothing to see in any log. Both are carried anyway, because
/// <c>Name</c> is the last segment of a nested tag and <c>Value</c> its full path, and a picker
/// wants to show the short one while submitting the long one.
/// </para>
/// </summary>
public record AdminImmichTagDto(Guid Id, string Value, string? Name);

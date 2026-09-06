/**
 * ImmichFrame.WebApi
 * 1.0
 * DO NOT MODIFY - This file has been generated using oazapfts.
 * See https://www.npmjs.com/package/oazapfts
 */
import * as Oazapfts from "@oazapfts/runtime";
import * as QS from "@oazapfts/runtime/query";
export const defaults: Oazapfts.Defaults<Oazapfts.CustomHeaders> = {
    headers: {},
    baseUrl: "/"
};
const oazapfts = Oazapfts.runtime(defaults);
export const servers = {};
export type AdminConfigSourceDto = {
    format?: string | null;
    path?: string | null;
    legacySchema?: boolean;
    editable?: boolean;
    notEditableReason?: string | null;
};
export type AdminGeneralSettingsDto = {
    interval?: number | null;
    transitionDuration?: number | null;
    downloadImages?: boolean | null;
    renewImagesDuration?: number | null;
    showClock?: boolean | null;
    clockFormat?: string | null;
    clockDateFormat?: string | null;
    showPhotoDate?: boolean | null;
    showProgressBar?: boolean | null;
    photoDateFormat?: string | null;
    showImageDesc?: boolean | null;
    showPeopleDesc?: boolean | null;
    showTagsDesc?: boolean | null;
    showAlbumName?: boolean | null;
    showImageLocation?: boolean | null;
    imageLocationFormat?: string | null;
    primaryColor?: string | null;
    secondaryColor?: string | null;
    style?: string | null;
    baseFontSize?: string | null;
    showWeatherDescription?: boolean | null;
    weatherIconUrl?: string | null;
    imageZoom?: boolean | null;
    imagePan?: boolean | null;
    imageFill?: boolean | null;
    playAudio?: boolean | null;
    layout?: string | null;
    language?: string | null;
    webcalendars?: string[] | null;
    refreshAlbumPeopleInterval?: number | null;
    weatherLatLong?: string | null;
    unitSystem?: string | null;
    weatherApiKey?: string | null;
    webhook?: string | null;
    authenticationSecret?: string | null;
    hasWeatherApiKey?: boolean;
    hasWebhook?: boolean;
    hasAuthenticationSecret?: boolean;
};
export type AdminAccountSettingsDto = {
    id?: string | null;
    immichServerUrl?: string | null;
    apiKey?: string | null;
    hasApiKey?: boolean;
    apiKeyFromFile?: boolean;
    apiKeyFile?: string | null;
    showMemories?: boolean | null;
    showFavorites?: boolean | null;
    showArchived?: boolean | null;
    showVideos?: boolean | null;
    imagesFromDays?: number | null;
    imagesFromDate?: string | null;
    imagesUntilDate?: string | null;
    albums?: string[] | null;
    excludedAlbums?: string[] | null;
    people?: string[] | null;
    tags?: string[] | null;
    rating?: number | null;
};
export type AdminConfigEntryDto = {
    name?: string | null;
    declaredKeys?: string[] | null;
    general?: AdminGeneralSettingsDto;
    accounts?: AdminAccountSettingsDto[] | null;
};
export type AdminConfigDto = {
    version?: string | null;
    source?: AdminConfigSourceDto;
    "default"?: AdminConfigEntryDto;
    profiles?: AdminConfigEntryDto[] | null;
};
export type AdminConfigUpdateDto = {
    version?: string | null;
    "default"?: AdminConfigEntryDto;
    profiles?: AdminConfigEntryDto[] | null;
    convertLegacySchema?: boolean;
};
export type AdminImmichAccountRefDto = {
    profile?: string | null;
    accountId?: string | null;
    version?: string | null;
    serverUrl?: string | null;
    apiKey?: string | null;
};
export type AdminImmichAlbumDto = {
    id?: string;
    albumName?: string | null;
    assetCount?: number;
};
export type AdminImmichPersonDto = {
    id?: string;
    name?: string | null;
};
export type AdminImmichPeopleDto = {
    people?: AdminImmichPersonDto[] | null;
    total?: number;
    truncated?: boolean;
};
export type AdminImmichTagDto = {
    id?: string;
    value?: string | null;
    name?: string | null;
};
export type AdminSessionDto = {
    configured?: boolean;
    authenticated?: boolean;
    isAdmin?: boolean;
    subject?: string | null;
    email?: string | null;
};
export type ExifResponseDto = {
    city?: string | null;
    country?: string | null;
    dateTimeOriginal?: string | null;
    description?: string | null;
    exifImageHeight?: number | null;
    exifImageWidth?: number | null;
    exposureTime?: string | null;
    fNumber?: number | null;
    fileSizeInByte?: number | null;
    focalLength?: number | null;
    iso?: number | null;
    latitude?: number | null;
    lensModel?: string | null;
    longitude?: number | null;
    make?: string | null;
    model?: string | null;
    modifyDate?: string | null;
    orientation?: string | null;
    projectionType?: string | null;
    rating?: number | null;
    state?: string | null;
    timeZone?: string | null;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type UserAvatarColor = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9;
export type UserResponseDto = {
    avatarColor: UserAvatarColor;
    email: string;
    id: string;
    name: string;
    profileChangedAt: string;
    profileImagePath: string;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type PersonResponseDto = {
    birthDate?: string | null;
    color?: string | null;
    id: string;
    isFavorite?: boolean | null;
    isHidden?: boolean;
    name: string;
    thumbnailPath: string;
    updatedAt?: string | null;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type AssetStackResponseDto = {
    assetCount?: number;
    id: string;
    primaryAssetId: string;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type TagResponseDto = {
    color?: string | null;
    createdAt: string;
    id: string;
    name: string;
    parentId?: string | null;
    updatedAt: string;
    value: string;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type AssetTypeEnum = 0 | 1 | 2 | 3;
export type AssetVisibility = 0 | 1 | 2 | 3;
export type AssetResponseDto = {
    immichServerUrl?: string | null;
    checksum: string;
    createdAt: string;
    duplicateId?: string | null;
    duration?: number | null;
    exifInfo?: ExifResponseDto;
    fileCreatedAt: string;
    fileModifiedAt: string;
    hasMetadata?: boolean;
    height?: number | null;
    id: string;
    isArchived?: boolean;
    isEdited?: boolean;
    isFavorite?: boolean;
    isOffline?: boolean;
    isTrashed?: boolean;
    libraryId?: string | null;
    livePhotoVideoId?: string | null;
    localDateTime: string;
    originalFileName: string;
    originalMimeType?: string | null;
    originalPath: string;
    owner?: UserResponseDto;
    ownerId: string;
    people?: PersonResponseDto[] | null;
    resized?: boolean | null;
    stack?: AssetStackResponseDto;
    tags?: TagResponseDto[] | null;
    thumbhash?: string | null;
    "type": AssetTypeEnum;
    updatedAt: string;
    visibility: AssetVisibility;
    width?: number | null;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type SourceType = 0 | 1 | 2;
export type AssetFaceResponseDto = {
    boundingBoxX1?: number;
    boundingBoxX2?: number;
    boundingBoxY1?: number;
    boundingBoxY2?: number;
    id: string;
    imageHeight?: number;
    imageWidth?: number;
    person?: PersonResponseDto;
    sourceType?: SourceType;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type AlbumUserRole = 0 | 1 | 2;
export type AlbumUserResponseDto = {
    role: AlbumUserRole;
    user: UserResponseDto;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type ContributorCountResponseDto = {
    assetCount?: number;
    userId: string;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type AssetOrder = 0 | 1;
export type AlbumResponseDto = {
    albumName: string;
    albumThumbnailAssetId?: string | null;
    albumUsers: AlbumUserResponseDto[];
    assetCount?: number;
    contributorCounts?: ContributorCountResponseDto[] | null;
    createdAt: string;
    description: string;
    endDate?: string | null;
    hasSharedLink?: boolean;
    id: string;
    isActivityEnabled?: boolean;
    lastModifiedAssetTimestamp?: string | null;
    order?: AssetOrder;
    shared?: boolean;
    startDate?: string | null;
    updatedAt: string;
    additionalProperties?: {
        [key: string]: any | null;
    } | null;
};
export type ProblemDetails = {
    "type"?: string | null;
    title?: string | null;
    status?: number | null;
    detail?: string | null;
    instance?: string | null;
    [key: string]: any;
};
export type ImageResponse = {
    randomImageBase64: string | null;
    thumbHashImageBase64: string | null;
    photoDate: string | null;
    imageLocation: string | null;
};
export type IAppointment = {
    startTime?: string;
    duration?: string;
    endTime?: string;
    summary?: string | null;
    description?: string | null;
    location?: string | null;
};
export type ClientSettingsDto = {
    interval?: number;
    transitionDuration?: number;
    downloadImages?: boolean;
    renewImagesDuration?: number;
    showClock?: boolean;
    clockFormat?: string | null;
    clockDateFormat?: string | null;
    showPhotoDate?: boolean;
    showProgressBar?: boolean;
    photoDateFormat?: string | null;
    showImageDesc?: boolean;
    showPeopleDesc?: boolean;
    showTagsDesc?: boolean;
    showAlbumName?: boolean;
    showImageLocation?: boolean;
    imageLocationFormat?: string | null;
    primaryColor?: string | null;
    secondaryColor?: string | null;
    style?: string | null;
    baseFontSize?: string | null;
    showWeatherDescription?: boolean;
    weatherIconUrl?: string | null;
    imageZoom?: boolean;
    imagePan?: boolean;
    imageFill?: boolean;
    playAudio?: boolean;
    layout?: string | null;
    language?: string | null;
};
export type IWeather = {
    location?: string | null;
    temperature?: number;
    unit?: string | null;
    temperatureUnit?: string | null;
    description?: string | null;
    iconId?: string | null;
};
export function getAdminConfig(opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminConfigDto;
    }>("/api/admin/config", {
        ...opts
    });
}
export function saveAdminConfig(adminConfigUpdateDto?: AdminConfigUpdateDto, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminConfigDto;
    }>("/api/admin/config", oazapfts.json({
        ...opts,
        method: "PUT",
        body: adminConfigUpdateDto
    }));
}
export function getAdminImmichAlbums(adminImmichAccountRefDto?: AdminImmichAccountRefDto, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminImmichAlbumDto[];
    }>("/api/admin/immich/albums", oazapfts.json({
        ...opts,
        method: "POST",
        body: adminImmichAccountRefDto
    }));
}
export function getAdminImmichPeople(adminImmichAccountRefDto?: AdminImmichAccountRefDto, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminImmichPeopleDto;
    }>("/api/admin/immich/people", oazapfts.json({
        ...opts,
        method: "POST",
        body: adminImmichAccountRefDto
    }));
}
export function getAdminImmichTags(adminImmichAccountRefDto?: AdminImmichAccountRefDto, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminImmichTagDto[];
    }>("/api/admin/immich/tags", oazapfts.json({
        ...opts,
        method: "POST",
        body: adminImmichAccountRefDto
    }));
}
export function getAdminImmichPersonThumbnail(id: string, { accountId, version, profile }: {
    accountId?: string;
    version?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchBlob<{
        status: 200;
        data: Blob;
    }>(`/api/admin/immich/people/${encodeURIComponent(id)}/thumbnail${QS.query(QS.explode({
        accountId,
        version,
        profile
    }))}`, {
        ...opts
    });
}
export function getAdminSession(opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AdminSessionDto;
    }>("/api/admin/session", {
        ...opts
    });
}
export function adminLogin({ returnUrl }: {
    returnUrl?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchText(`/api/admin/login${QS.query(QS.explode({
        returnUrl
    }))}`, {
        ...opts
    });
}
export function adminLogout(opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchText("/api/admin/logout", {
        ...opts,
        method: "POST"
    });
}
export function getAssets({ clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AssetResponseDto[];
    }>(`/api/Asset${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getAssetInfo(id: string, { clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AssetResponseDto;
    }>(`/api/Asset/${encodeURIComponent(id)}/AssetInfo${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getAssetFaces(id: string, { clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AssetFaceResponseDto[];
    }>(`/api/Asset/${encodeURIComponent(id)}/AssetFaces${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getAlbumInfo(id: string, { clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: AlbumResponseDto[];
    }>(`/api/Asset/${encodeURIComponent(id)}/AlbumInfo${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getImage(id: string, { clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchBlob<{
        status: 200;
        data: Blob;
    }>(`/api/Asset/${encodeURIComponent(id)}/Image${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getAsset(id: string, { clientIdentifier, profile, assetType }: {
    clientIdentifier?: string;
    profile?: string;
    assetType?: AssetTypeEnum;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchBlob<{
        status: 200;
        data: Blob;
    } | {
        status: 206;
        data: Blob;
    } | {
        status: 416;
        data: ProblemDetails;
    }>(`/api/Asset/${encodeURIComponent(id)}/Asset${QS.query(QS.explode({
        clientIdentifier,
        profile,
        assetType
    }))}`, {
        ...opts
    });
}
export function getRandomImageAndInfo({ clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: ImageResponse;
    }>(`/api/Asset/RandomImageAndInfo${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getAppointments({ clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: IAppointment[];
    }>(`/api/Calendar${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getConfig({ clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: ClientSettingsDto;
    }>(`/api/Config${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}
export function getVersion(opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: string;
    }>("/api/Config/Version", {
        ...opts
    });
}
export function getWeather({ clientIdentifier, profile }: {
    clientIdentifier?: string;
    profile?: string;
} = {}, opts?: Oazapfts.RequestOpts) {
    return oazapfts.fetchJson<{
        status: 200;
        data: IWeather;
    }>(`/api/Weather${QS.query(QS.explode({
        clientIdentifier,
        profile
    }))}`, {
        ...opts
    });
}

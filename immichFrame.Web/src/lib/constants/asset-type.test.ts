import { describe, expect, it } from 'vitest';
import { AssetTypeEnum, type AssetResponseDto } from '$lib/immichFrameApi';
import { isSupportedAsset } from './asset-type';

describe('isSupportedAsset', () => {
	it.each([
		{ type: AssetTypeEnum.Image, supported: true },
		{ type: AssetTypeEnum.Video, supported: true },
		{ type: AssetTypeEnum.Audio, supported: false },
		{ type: AssetTypeEnum.Other, supported: false }
	])("an asset of type '$type', is supported: $supported", ({ type, supported }) => {
		// Act
		const isSupported = isSupportedAsset({ type } as AssetResponseDto);

		// Assert
		expect(isSupported).toBe(supported);
	});
});

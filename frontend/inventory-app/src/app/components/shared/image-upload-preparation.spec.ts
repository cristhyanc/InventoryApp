import {
  JPEG_QUALITY,
  MAX_IMAGE_DIMENSION,
  UNTOUCHED_IMAGE_SIZE_LIMIT_BYTES,
  prepareImageForUpload
} from './image-upload-preparation';
import { FakeImageDecoding, fileOfSize, installFakeImageDecoding } from './image-upload-preparation.spec-helpers';

describe('prepareImageForUpload (issue #1161)', () => {
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  it('resizes a 4000x3000 phone photo to 2000 px and uploads the JPEG it encoded', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
    const photo = fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg');

    const prepared = await prepareImageForUpload(photo);

    expect(prepared).not.toBe(photo);
    expect(prepared.name).toBe('IMG_4312.jpg');
    expect(prepared.type).toBe('image/jpeg');
    expect(prepared.size).toBe(450_000);
    expect(prepared.size).toBeLessThan(photo.size);
    expect(decoding.draws).toEqual([
      { canvasWidth: MAX_IMAGE_DIMENSION, canvasHeight: 1500, width: MAX_IMAGE_DIMENSION, height: 1500 }
    ]);
    expect(decoding.encodeCalls).toEqual([{ type: 'image/jpeg', quality: JPEG_QUALITY }]);
  });

  it('asks the browser for the photo the camera orientation flag describes, so a rotated photo comes out upright', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 3000, height: 4000 }, encodedBytes: 300_000 });

    await prepareImageForUpload(fileOfSize(3 * 1024 * 1024, 'rotated.jpg', 'image/jpeg'));

    expect(decoding.decodeCalls).toHaveLength(1);
    expect(decoding.decodeCalls[0].options).toEqual({ imageOrientation: 'from-image' });
    // The portrait photo is scaled on its own longest side, which is its height.
    expect(decoding.draws).toEqual([
      { canvasWidth: 1500, canvasHeight: MAX_IMAGE_DIMENSION, width: 1500, height: MAX_IMAGE_DIMENSION }
    ]);
  });

  it('releases the decoded bitmap the browser would otherwise keep in memory', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 400_000 });

    await prepareImageForUpload(fileOfSize(4 * 1024 * 1024, 'photo.jpg', 'image/jpeg'));

    expect(decoding.released()).toBe(1);
  });

  it('leaves a PDF byte-for-byte alone and never decodes it', async () => {
    decoding = installFakeImageDecoding();
    const pdf = fileOfSize(3 * 1024 * 1024, 'tax-invoice.pdf', 'application/pdf');

    expect(await prepareImageForUpload(pdf)).toBe(pdf);
    expect(decoding.decodeCalls).toEqual([]);
    expect(decoding.encodeCalls).toEqual([]);
  });

  it('leaves a PDF alone even when the browser reported no content type for it', async () => {
    decoding = installFakeImageDecoding();
    const pdf = fileOfSize(2048, 'statement.PDF');

    expect(await prepareImageForUpload(pdf)).toBe(pdf);
    expect(decoding.decodeCalls).toEqual([]);
  });

  it('leaves a 600 KB 1200x900 PNG exactly as it is', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 1200, height: 900 }, encodedBytes: 1 });
    const small = fileOfSize(600 * 1024, 'screenshot.png', 'image/png');

    expect(await prepareImageForUpload(small)).toBe(small);
    expect(decoding.encodeCalls).toEqual([]);
  });

  it('re-encodes an image that is within 2000 px but over the untouched size limit', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 1500, height: 1000 }, encodedBytes: 200_000 });
    const heavy = fileOfSize(UNTOUCHED_IMAGE_SIZE_LIMIT_BYTES + 1, 'scan.png', 'image/png');

    const prepared = await prepareImageForUpload(heavy);

    expect(prepared.name).toBe('scan.jpg');
    expect(prepared.type).toBe('image/jpeg');
    // Nothing is scaled up: a 1500x1000 photo is drawn at its own size.
    expect(decoding.draws).toEqual([{ canvasWidth: 1500, canvasHeight: 1000, width: 1500, height: 1000 }]);
  });

  it('uploads the original when the JPEG it encoded is no smaller', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 1500, height: 1000 }, encodedBytes: 3 * 1024 * 1024 });
    const original = fileOfSize(2 * 1024 * 1024, 'poster.png', 'image/png');

    expect(await prepareImageForUpload(original)).toBe(original);
  });

  it('converts a HEIC photo the browser can decode to a .jpg under its own base name', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 3024, height: 4032 }, encodedBytes: 480_000 });
    // Chrome reports no content type for a HEIC it cannot read; the name is what identifies it.
    const heic = fileOfSize(3_900_000, 'IMG_0007.HEIC');

    const prepared = await prepareImageForUpload(heic);

    expect(prepared.name).toBe('IMG_0007.jpg');
    expect(prepared.type).toBe('image/jpeg');
    expect(prepared.size).toBe(480_000);
  });

  it('converts a small HEIC too, and keeps the JPEG even when it comes out larger', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 1000, height: 800 }, encodedBytes: 90_000 });
    const heic = fileOfSize(40_000, 'photo.heif', 'image/heif');

    const prepared = await prepareImageForUpload(heic);

    expect(prepared.name).toBe('photo.jpg');
    expect(prepared.type).toBe('image/jpeg');
    expect(prepared.size).toBe(90_000);
  });

  it('uploads the original HEIC unchanged when the browser cannot decode it', async () => {
    decoding = installFakeImageDecoding({ decoded: 'undecodable' });
    const heic = fileOfSize(3_400_000, 'IMG_0008.heic', 'image/heic');

    await expect(prepareImageForUpload(heic)).resolves.toBe(heic);
  });

  it('uploads the original when the browser has no image decoder at all', async () => {
    decoding = installFakeImageDecoding({ decoder: 'absent' });
    const photo = fileOfSize(4 * 1024 * 1024, 'photo.jpg', 'image/jpeg');

    await expect(prepareImageForUpload(photo)).resolves.toBe(photo);
  });

  it('uploads the original when the browser offers no 2D canvas context', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, context: 'missing' });
    const photo = fileOfSize(4 * 1024 * 1024, 'photo.jpg', 'image/jpeg');

    await expect(prepareImageForUpload(photo)).resolves.toBe(photo);
  });

  it('uploads the original when the encoder produces nothing', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: null });
    const photo = fileOfSize(4 * 1024 * 1024, 'photo.jpg', 'image/jpeg');

    await expect(prepareImageForUpload(photo)).resolves.toBe(photo);
  });

  it('keeps a name with no extension and never produces an empty one', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 100 });

    const named = await prepareImageForUpload(fileOfSize(2 * 1024 * 1024, 'receipt', 'image/jpeg'));
    expect(named.name).toBe('receipt.jpg');

    const dotted = await prepareImageForUpload(fileOfSize(2 * 1024 * 1024, '.heic'));
    expect(dotted.name).toBe('.heic.jpg');
  });
});

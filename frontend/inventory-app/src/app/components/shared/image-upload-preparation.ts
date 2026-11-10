/**
 * Shrinks and converts a photo in the browser before it is uploaded (issue #1161).
 *
 * A phone photo is routinely 3-8 MB, and on an iPhone it is HEIC - a format Chrome and Firefox
 * cannot read at all. Every attachment upload (New Purchase, the purchase attach-document row, and
 * an operating expense's supporting document) therefore passes the chosen file through this one
 * helper first, so that what travels to the server, what the page shows as the file's size, and
 * what counts against the business's attachment storage quota are all the same bytes.
 *
 * **No server rule changes.** The 10 MiB ceiling and the allowed extensions of
 * `PurchaseDocumentPolicy` and `ExpenseAttachmentPolicy` are untouched and still decide: this only
 * makes the file that is sent smaller, and a file it cannot improve is sent exactly as it was.
 *
 * **It never blocks the person.** A format the browser cannot decode, a browser with no image
 * decoder, a canvas that hands back no context and an encoder that produces nothing all return the
 * original file unchanged and report nothing: the server's own rules are what refuse a file, and a
 * failed optimisation is not a reason to stop somebody recording what they bought.
 *
 * **HEIC is converted only where the browser can decode it** - Safari today; Chrome and Firefox
 * cannot, and this change deliberately adds no decoding library (and so no new npm dependency or
 * third-party notice). A HEIC that cannot be decoded is uploaded as it is, which is what the server
 * already accepts.
 */

/** The longest side a re-encoded photo may have, in pixels. */
export const MAX_IMAGE_DIMENSION = 2000;

/** The JPEG quality a re-encoded photo is written with. */
export const JPEG_QUALITY = 0.8;

/**
 * Below this, an image that is already within {@link MAX_IMAGE_DIMENSION} is left exactly as it is:
 * re-encoding it would cost quality for a saving nobody's storage allowance would notice.
 */
export const UNTOUCHED_IMAGE_SIZE_LIMIT_BYTES = 1024 * 1024;

const JPEG_CONTENT_TYPE = 'image/jpeg';

/** The image extensions this helper recognises when the browser reported no content type. */
const IMAGE_EXTENSIONS = ['.jpg', '.jpeg', '.png', '.webp', '.heic', '.heif'];

/** The HEIC/HEIF extensions, which are what an iPhone photo arrives as. */
const HEIC_EXTENSIONS = ['.heic', '.heif'];

/**
 * The file to upload for the one the person chose: a JPEG of at most
 * {@link MAX_IMAGE_DIMENSION} px on its longest side when that is smaller than what they chose (or
 * when what they chose is HEIC), and otherwise the original file itself.
 */
export async function prepareImageForUpload(file: File): Promise<File> {
  if (!isPreparableImage(file)) {
    return file;
  }

  try {
    return await reencode(file);
  } catch {
    // A format this browser cannot decode, or any other failure of the optimisation. The original
    // is uploaded unchanged and the server's own rules decide, exactly as before this helper.
    return file;
  }
}

async function reencode(file: File): Promise<File> {
  if (typeof createImageBitmap !== 'function') {
    return file;
  }

  // `from-image` is what applies the camera's orientation flag, so a photo taken sideways is
  // decoded the way up it is meant to be read rather than the way the sensor recorded it.
  const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  const heic = isHeic(file);

  try {
    const longestSide = Math.max(bitmap.width, bitmap.height);
    const scale = longestSide > MAX_IMAGE_DIMENSION ? MAX_IMAGE_DIMENSION / longestSide : 1;
    if (!heic && scale === 1 && file.size < UNTOUCHED_IMAGE_SIZE_LIMIT_BYTES) {
      return file;
    }

    const encoded = await encodeJpeg(bitmap, scale);
    // A JPEG that saved nothing is not worth the quality it cost - unless the original is a HEIC,
    // which has to be converted whatever it weighs so that every part of the app can read it.
    if (encoded === null || (!heic && encoded.size >= file.size)) {
      return file;
    }

    return new File([encoded], jpegFileName(file.name), {
      type: JPEG_CONTENT_TYPE,
      lastModified: file.lastModified
    });
  } finally {
    bitmap.close();
  }
}

async function encodeJpeg(bitmap: ImageBitmap, scale: number): Promise<Blob | null> {
  const width = Math.max(1, Math.round(bitmap.width * scale));
  const height = Math.max(1, Math.round(bitmap.height * scale));

  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;

  const context = canvas.getContext('2d');
  if (context === null) {
    return null;
  }

  context.drawImage(bitmap, 0, 0, width, height);
  return new Promise<Blob | null>(resolve =>
    canvas.toBlob(blob => resolve(blob), JPEG_CONTENT_TYPE, JPEG_QUALITY));
}

/**
 * Whether this is a file worth decoding at all. A PDF - the other thing these uploads accept - is
 * never touched, and neither is anything the browser and the file name agree is not an image.
 */
function isPreparableImage(file: File): boolean {
  if (file.type === 'application/pdf') {
    return false;
  }
  if (file.type.startsWith('image/')) {
    return true;
  }
  return IMAGE_EXTENSIONS.includes(extensionOf(file.name));
}

/** Whether this is an iPhone photo: a browser that cannot read HEIC reports no type for one. */
function isHeic(file: File): boolean {
  return HEIC_EXTENSIONS.includes(extensionOf(file.name)) ||
    file.type === 'image/heic' || file.type === 'image/heif';
}

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot).toLowerCase();
}

/** The same base name the person chose, as a `.jpg` - the extension the bytes now are. */
function jpegFileName(name: string): string {
  const dot = name.lastIndexOf('.');
  const base = dot > 0 ? name.slice(0, dot) : name;
  return `${base}.jpg`;
}

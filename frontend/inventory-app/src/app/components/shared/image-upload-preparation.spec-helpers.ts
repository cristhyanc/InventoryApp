/**
 * The browser halves of `prepareImageForUpload` (issue #1161), as a spec can supply them.
 *
 * jsdom implements neither `createImageBitmap` nor a canvas 2D context, so a photo cannot be
 * decoded, drawn or encoded under Jest at all. This installs both seams and records what the
 * helper asked of them, which is what lets a spec prove the decisions the helper itself makes -
 * the size it draws at, the type and quality it encodes with, which file it finally returns - while
 * the pixels stay the browser's business.
 */
export interface DecodedPhoto {
  width: number;
  height: number;
}

/** One `createImageBitmap` call: the blob handed over and the options it was given. */
export interface DecodeCall {
  blob: Blob;
  options?: ImageBitmapOptions;
}

/** One `drawImage` call: the canvas it drew on and the size it drew the photo at. */
export interface DrawCall {
  canvasWidth: number;
  canvasHeight: number;
  width: number;
  height: number;
}

/** One `toBlob` call: the type and quality the helper asked the browser to encode with. */
export interface EncodeCall {
  type?: string;
  quality?: number;
}

export interface FakeImageDecoding {
  readonly decodeCalls: DecodeCall[];
  readonly draws: DrawCall[];
  readonly encodeCalls: EncodeCall[];
  /** How many decoded bitmaps were released, which a browser keeps in memory until they are. */
  readonly released: () => number;
  /**
   * Lets the decode at `index` (in `decodeCalls` order) finish, for deferred decoding only. This
   * is how a spec holds one photo's preparation open while the form that chose it moves on, and
   * how it chooses the order two overlapping preparations finish in.
   */
  releaseDecode(index: number): void;
  restore(): void;
}

export interface FakeImageDecodingOptions {
  /** The photo's decoded pixel size, or `'undecodable'` for a format this browser cannot read. */
  decoded?: DecodedPhoto | 'undecodable';
  /** The size in bytes of the JPEG the encoder produces, or `null` when it produces nothing. */
  encodedBytes?: number | null;
  /** `'missing'` for a browser that hands back no 2D context at all. */
  context?: 'missing';
  /** `'absent'` for a browser with no `createImageBitmap`. */
  decoder?: 'absent';
  /**
   * `'deferred'` holds every decode open until {@link FakeImageDecoding.releaseDecode} finishes it,
   * which is how a spec proves what happens while a photo is still being prepared and what happens
   * when its preparation lands after the form that started it has moved on.
   */
  completion?: 'deferred';
}

interface MutableGlobal {
  createImageBitmap?: unknown;
}

export function installFakeImageDecoding(options: FakeImageDecodingOptions = {}): FakeImageDecoding {
  const decoded = options.decoded ?? { width: 100, height: 100 };
  const encodedBytes = options.encodedBytes === undefined ? 1024 : options.encodedBytes;

  const decodeCalls: DecodeCall[] = [];
  const draws: DrawCall[] = [];
  const encodeCalls: EncodeCall[] = [];
  /** The decodes still held open, by call order; a released one is cleared from the slot. */
  const heldDecodes: ((() => void) | undefined)[] = [];
  let released = 0;

  const globals = globalThis as MutableGlobal;
  const hadDecoder = 'createImageBitmap' in globals;
  const realDecoder = globals.createImageBitmap;
  const realGetContext = HTMLCanvasElement.prototype.getContext;
  const realToBlob = HTMLCanvasElement.prototype.toBlob;

  if (options.decoder === 'absent') {
    delete globals.createImageBitmap;
  } else {
    globals.createImageBitmap = (blob: Blob, bitmapOptions?: ImageBitmapOptions): Promise<ImageBitmap> => {
      decodeCalls.push({ blob, options: bitmapOptions });
      return new Promise<ImageBitmap>((resolve, reject) => {
        const finish = () => {
          if (decoded === 'undecodable') {
            reject(new Error('The source image cannot be decoded.'));
            return;
          }
          resolve({
            width: decoded.width,
            height: decoded.height,
            close: () => {
              released++;
            }
          } as unknown as ImageBitmap);
        };

        if (options.completion === 'deferred') {
          heldDecodes.push(finish);
        } else {
          finish();
        }
      });
    };
  }

  HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, contextId: string) {
    if (contextId !== '2d' || options.context === 'missing') {
      return null;
    }
    // The canvas is sized before its context is asked for, so this is the size it was drawn at.
    const { width: canvasWidth, height: canvasHeight } = this;
    return {
      drawImage: (_image: unknown, _x: number, _y: number, width: number, height: number) => {
        draws.push({ canvasWidth, canvasHeight, width, height });
      }
    } as unknown as CanvasRenderingContext2D;
  } as typeof HTMLCanvasElement.prototype.getContext;

  HTMLCanvasElement.prototype.toBlob = function (
    callback: BlobCallback, type?: string, quality?: unknown): void {
    encodeCalls.push({ type, quality: typeof quality === 'number' ? quality : undefined });
    callback(encodedBytes === null ? null : new Blob(['j'.repeat(encodedBytes)], { type: type ?? '' }));
  };

  return {
    decodeCalls,
    draws,
    encodeCalls,
    released: () => released,
    releaseDecode: (index: number) => {
      const finish = heldDecodes[index];
      if (finish === undefined) {
        throw new Error(`No decode is being held open at index ${index}.`);
      }
      heldDecodes[index] = undefined;
      finish();
    },
    restore: () => {
      if (hadDecoder) {
        globals.createImageBitmap = realDecoder;
      } else {
        delete globals.createImageBitmap;
      }
      HTMLCanvasElement.prototype.getContext = realGetContext;
      HTMLCanvasElement.prototype.toBlob = realToBlob;
    }
  };
}

/** A file of an exact size, which is what every rule in the helper is decided on. */
export function fileOfSize(bytes: number, name: string, type = ''): File {
  return new File([new Uint8Array(bytes)], name, { type });
}

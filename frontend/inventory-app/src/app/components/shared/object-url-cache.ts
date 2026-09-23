/**
 * Tracks the object URLs a component creates for protected documents fetched through
 * `HttpClient`, so that every one of them is revoked when the component no longer needs it:
 * a browser keeps the underlying blob in memory until its object URL is revoked.
 *
 * A URL that has already been loaded into a tab or an `<img>` keeps rendering after it is
 * revoked, so releasing on component destroy is safe for documents opened in a new tab.
 */
export class ObjectUrlCache {
  private urls: string[] = [];

  create(blob: Blob): string {
    const url = URL.createObjectURL(blob);
    this.urls.push(url);
    return url;
  }

  releaseAll(): void {
    this.urls.forEach(url => URL.revokeObjectURL(url));
    this.urls = [];
  }
}

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The browser tab identifies the product as Restockroo (issue #1071). `index.html`'s `<title>`
 * is the document title Angular starts with on every direct load or refresh of an authenticated
 * app route - the landing page (`landing-page.component.ts`) only ever overrides it temporarily
 * and restores whatever this held, so a retired name here would resurface on every landing-to-app
 * navigation too.
 */
const indexHtml = readFileSync(join(__dirname, '..', 'index.html'), 'utf8');

describe('Document title (issue #1071)', () => {
  it('is exactly Restockroo in index.html', () => {
    expect(indexHtml).toMatch(/<title>Restockroo<\/title>/);
  });

  it('no longer carries the retired Inventory Manager title', () => {
    expect(indexHtml).not.toContain('Inventory Manager');
  });
});

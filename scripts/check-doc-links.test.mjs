// Deterministic tests for the documentation link checker (issue #1260).
// Run with: node --test scripts/check-doc-links.test.mjs
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { describe, it } from 'node:test';

import { checkDocumentationLinks, extractAnchors, extractLinks, listDocumentationFiles, main, repositoryRoot, slugify } from './check-doc-links.mjs';

// --- slugify -----------------------------------------------------------------------------

describe('slugify', () => {
  it('lower-cases and turns spaces into hyphens', () => {
    assert.equal(slugify('Database and migrations'), 'database-and-migrations');
  });

  it('drops punctuation without inserting a space for it', () => {
    assert.equal(
      slugify('Provider identity on the local row (issues #732, #759, #760 and #735)'),
      'provider-identity-on-the-local-row-issues-732-759-760-and-735',
    );
  });

  it('drops a period glued to a leading number without separating it from the next word', () => {
    assert.equal(slugify('5. Test API application settings'), '5-test-api-application-settings');
  });

  it('keeps an existing hyphen and a digit run', () => {
    assert.equal(slugify('Single-provider fallback route'), 'single-provider-fallback-route');
  });

  it('drops inline code backticks, keeping their contents', () => {
    assert.equal(slugify('The `ReceiptScanning__ApiKey` setting'), 'the-receiptscanning__apikey-setting');
  });
});

// --- extractAnchors ------------------------------------------------------------------------

describe('extractAnchors', () => {
  it('collects one anchor per heading, at every level', () => {
    const anchors = extractAnchors('# Title\n\n## Section one\n\n### Subsection\n');
    assert.deepEqual([...anchors].sort(), ['section-one', 'subsection', 'title']);
  });

  it('appends GitHub\'s own -1, -2 suffix to a slug repeated later in the document', () => {
    const anchors = extractAnchors('## Notes\n\ntext\n\n## Notes\n\nmore\n\n## Notes\n');
    assert.deepEqual([...anchors].sort(), ['notes', 'notes-1', 'notes-2']);
  });

  it('ignores a heading-shaped line inside a fenced code block', () => {
    const anchors = extractAnchors('## Real heading\n\n```text\n## Not a heading\n```\n');
    assert.deepEqual([...anchors], ['real-heading']);
  });

  it('ignores an ATX closing sequence and trailing whitespace in the heading text', () => {
    const anchors = extractAnchors('## Section one ##\n\n### Section two\t\n');
    assert.deepEqual([...anchors].sort(), ['section-one', 'section-two']);
  });

  it('produces no anchor for a heading whose text is only whitespace', () => {
    const anchors = extractAnchors('##   \n\n## Real heading\n');
    assert.deepEqual([...anchors], ['real-heading']);
  });

  it('ignores a heading-shaped line inside a tilde-fenced code block', () => {
    const anchors = extractAnchors('## Real heading\n\n~~~bash\n# not a heading either\n~~~\n');
    assert.deepEqual([...anchors], ['real-heading']);
  });
});

// --- extractLinks --------------------------------------------------------------------------

describe('extractLinks', () => {
  it('extracts a plain inline link with its line number', () => {
    const links = extractLinks('intro\n\nSee [the page](./page.md#section) for more.\n');
    assert.deepEqual(links, [{ line: 3, target: './page.md#section' }]);
  });

  it('ignores an image', () => {
    const links = extractLinks('![alt text](./image.png)\n');
    assert.deepEqual(links, []);
  });

  it('ignores a link inside a fenced code block', () => {
    const links = extractLinks('```text\n[example](./nowhere.md)\n```\n');
    assert.deepEqual(links, []);
  });

  it('follows link text that wraps across a hand-wrapped line, without crossing the target itself', () => {
    const markdown = 'See [a purchase without a\nreceipt](#a-purchase-without-a-receipt) for detail.\n';
    const links = extractLinks(markdown);
    assert.deepEqual(links, [{ line: 1, target: '#a-purchase-without-a-receipt' }]);
  });

  it('extracts the target of a link whose text contains an opening bracket', () => {
    const links = extractLinks('Odd [text [with a bracket](./page.md) here.\n');
    assert.deepEqual(links, [{ line: 1, target: './page.md' }]);
  });

  it('extracts nothing from an unclosed bracket or parenthesis', () => {
    assert.deepEqual(extractLinks('A stray [ bracket and a stray ( parenthesis.\n'), []);
    assert.deepEqual(extractLinks('[unclosed text(./page.md)\n'), []);
  });

  it('extracts two links on the same line independently', () => {
    const links = extractLinks('[A](#a) and [B](#b)\n');
    assert.deepEqual(links, [
      { line: 1, target: '#a' },
      { line: 1, target: '#b' },
    ]);
  });
});

// --- checkDocumentationLinks ----------------------------------------------------------------

describe('checkDocumentationLinks', () => {
  function withFixture(files, callback) {
    const root = mkdtempSync(join(tmpdir(), 'check-doc-links-'));
    try {
      for (const [relativePath, content] of Object.entries(files)) {
        const fullPath = join(root, relativePath);
        mkdirSync(dirname(fullPath), { recursive: true });
        writeFileSync(fullPath, content);
      }
      return callback(root);
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }

  it('accepts a same-file anchor that matches a real heading', () => {
    withFixture({ 'a.md': '## Section one\n\nSee [above](#section-one).\n' }, (root) => {
      assert.deepEqual(checkDocumentationLinks(['a.md'], root), []);
    });
  });

  it('reports a same-file anchor that matches no heading', () => {
    withFixture({ 'a.md': '## Section one\n\nSee [above](#section-two).\n' }, (root) => {
      const broken = checkDocumentationLinks(['a.md'], root);
      assert.equal(broken.length, 1);
      assert.equal(broken[0].file, 'a.md');
      assert.match(broken[0].reason, /no heading produces anchor "#section-two"/);
    });
  });

  it('accepts a cross-file link with a correct anchor', () => {
    withFixture(
      {
        'a.md': 'See [the other page](b.md#target-section).\n',
        'b.md': '## Target section\n',
      },
      (root) => {
        assert.deepEqual(checkDocumentationLinks(['a.md', 'b.md'], root), []);
      },
    );
  });

  it('reports a cross-file link whose target file does not exist', () => {
    withFixture({ 'a.md': 'See [missing](missing.md).\n' }, (root) => {
      const broken = checkDocumentationLinks(['a.md'], root);
      assert.equal(broken.length, 1);
      assert.match(broken[0].reason, /target does not exist: missing\.md/);
    });
  });

  it('reports a cross-file link whose anchor does not exist in the target file', () => {
    withFixture(
      {
        'a.md': 'See [the other page](b.md#not-there).\n',
        'b.md': '## Target section\n',
      },
      (root) => {
        const broken = checkDocumentationLinks(['a.md'], root);
        assert.equal(broken.length, 1);
        assert.match(broken[0].reason, /no heading produces anchor "#not-there" in b\.md/);
      },
    );
  });

  it('resolves a link relative to a nested file\'s own directory', () => {
    withFixture(
      {
        'docs/sub/a.md': 'See [the root file](../../AGENTS.md#database-and-migrations).\n',
        'AGENTS.md': '## Database and migrations\n',
      },
      (root) => {
        assert.deepEqual(checkDocumentationLinks(['docs/sub/a.md'], root), []);
      },
    );
  });

  it('never checks an external link, even one with an unresolvable path', () => {
    withFixture(
      { 'a.md': 'See [GitHub](https://github.com/example/repo#readme) and [email](mailto:a@example.com).\n' },
      (root) => {
        assert.deepEqual(checkDocumentationLinks(['a.md'], root), []);
      },
    );
  });

  it('still checks a non-Markdown target for existence, even though it checks no anchor on it', () => {
    withFixture(
      {
        'a.md': 'See [the workflow](missing.yml#anything-at-all).\n',
      },
      (root) => {
        const broken = checkDocumentationLinks(['a.md'], root);
        assert.equal(broken.length, 1);
        assert.match(broken[0].reason, /target does not exist: missing\.yml/);
      },
    );
  });

  it('accepts any anchor at all on an existing non-Markdown target', () => {
    withFixture(
      {
        'a.md': 'See [the script](script.sh#nonsense-anchor).\n',
        'script.sh': '#!/usr/bin/env bash\n',
      },
      (root) => {
        assert.deepEqual(checkDocumentationLinks(['a.md'], root), []);
      },
    );
  });

  it('accepts a link to an existing directory', () => {
    withFixture(
      {
        'a.md': 'See [the folder](folder/).\n',
        'folder/.keep': '',
      },
      (root) => {
        assert.deepEqual(checkDocumentationLinks(['a.md'], root), []);
      },
    );
  });

  it('reports the real repository\'s hand-maintained documentation as free of broken links', () => {
    const broken = checkDocumentationLinks(listDocumentationFiles());
    assert.deepEqual(broken, []);
  });
});

// --- listDocumentationFiles -----------------------------------------------------------------

describe('listDocumentationFiles', () => {
  it('lists the five root files and every Markdown file under docs/, recursively', () => {
    const files = listDocumentationFiles(repositoryRoot);
    for (const expected of ['README.md', 'AGENTS.md', 'CLAUDE.md', 'CONTRIBUTING.md', 'SECURITY.md', 'docs/automation.md', 'docs/architecture.md']) {
      assert.ok(files.includes(expected), `expected ${expected} in ${JSON.stringify(files)}`);
    }
    assert.ok(files.every((file) => file.endsWith('.md')));
  });

  it('lists them in code-unit order, so the scan order does not depend on the locale', () => {
    const files = listDocumentationFiles(repositoryRoot);
    for (let index = 1; index < files.length; index += 1) {
      assert.ok(files[index - 1] < files[index], `${files[index - 1]} must sort before ${files[index]}`);
    }
  });
});

// --- Command line -----------------------------------------------------------------------

describe('main', () => {
  function capture(callback) {
    const out = [];
    const err = [];
    const originalLog = console.log;
    const originalError = console.error;
    console.log = (...args) => out.push(args.join(' '));
    console.error = (...args) => err.push(args.join(' '));
    try {
      return { code: callback(), out, err };
    } finally {
      console.log = originalLog;
      console.error = originalError;
    }
  }

  it('exits 0 for this repository\'s own documentation', () => {
    const result = capture(() => main());
    assert.equal(result.code, 0);
    assert.ok(result.out.some((line) => /no broken relative links or anchors/.test(line)));
  });
});

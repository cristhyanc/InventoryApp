import * as fs from 'fs';
import * as path from 'path';
import { CAPABILITIES } from '../access/capabilities';
import { PREVIEWABLE_ROLES } from './role-preview';

/** Every file the role preview is made of, as source. */
const PREVIEW_SOURCES = [
  'role-preview/role-preview.ts',
  'role-preview/role-preview.service.ts',
  'role-preview/role-preview.interceptor.ts',
  'layout/role-preview-banner.component.ts',
  'layout/role-preview-menu.component.ts'
];

function read(relativePath: string): string {
  return fs.readFileSync(path.join(__dirname, '..', relativePath), 'utf8');
}

/** The `import ... from '<specifier>'` specifiers of one source file. */
function importsOf(source: string): string[] {
  return Array.from(source.matchAll(/from\s+'([^']+)'/g)).map((match) => match[1]);
}

/**
 * The preview's capabilities come from `GET /api/me/access` and from nowhere else (issue #721).
 *
 * This is a source check rather than a behavioural one on purpose: the defect it guards against is
 * somebody computing "what an Operator may do" in the browser, which would be invisible in a test
 * that stubs the answer and would silently diverge from
 * `Inventory.Application.Access.RoleCapabilities` the first time a capability moved.
 */
describe('the role preview reads no frontend role table (issue #721)', () => {
  it.each(PREVIEW_SOURCES)('does not import role-definitions.ts into %s', (file) => {
    expect(importsOf(read(file)).filter((specifier) => specifier.includes('role-definitions'))).toEqual([]);
  });

  it.each(PREVIEW_SOURCES)('names no capability of its own in %s', (file) => {
    const source = read(file);

    expect(CAPABILITIES.filter((capability) => source.includes(`'${capability}'`))).toEqual([]);
  });

  /** The companion rule, asserted from the other side: the vocabulary still holds no role. */
  it('keeps capabilities.ts free of every role name', () => {
    const source = read('access/capabilities.ts');

    expect(PREVIEWABLE_ROLES.filter((role) => source.includes(`'${role}'`))).toEqual([]);
  });
});

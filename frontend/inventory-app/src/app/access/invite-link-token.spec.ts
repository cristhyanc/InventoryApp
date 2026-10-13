import { extractInviteToken } from './invite-link-token';

/**
 * The welcome and rejected pages' "I have an invite link" field (issue #542) accepts either the
 * full `/invite/<token>` link or the bare token, and hands whatever it extracts to that route -
 * #535's acceptance page and the redemption endpoint behind it are the actual authority on whether
 * it is usable, so this never validates a token's shape itself.
 */
describe('extractInviteToken (issue #542)', () => {
  it('extracts the token from a full invite link', () => {
    expect(extractInviteToken('https://app.example.com/invite/abc123')).toBe('abc123');
  });

  it('extracts the token from a full invite link carrying a trailing slash, query or fragment', () => {
    expect(extractInviteToken('https://app.example.com/invite/abc123/')).toBe('abc123');
    expect(extractInviteToken('https://app.example.com/invite/abc123?utm_source=email')).toBe('abc123');
    expect(extractInviteToken('https://app.example.com/invite/abc123#top')).toBe('abc123');
  });

  it('extracts the token from a path-only invite link', () => {
    expect(extractInviteToken('/invite/abc123')).toBe('abc123');
  });

  it('treats a bare value with no /invite/ segment as the token itself', () => {
    expect(extractInviteToken('abc123')).toBe('abc123');
  });

  it('trims surrounding whitespace from a pasted value', () => {
    expect(extractInviteToken('  abc123  ')).toBe('abc123');
    expect(extractInviteToken('  https://app.example.com/invite/abc123  ')).toBe('abc123');
  });

  it('returns null for blank input', () => {
    expect(extractInviteToken('')).toBeNull();
    expect(extractInviteToken('   ')).toBeNull();
  });
});

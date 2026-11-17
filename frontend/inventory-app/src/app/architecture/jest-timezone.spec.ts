describe('Jest runs in a fixed UTC zone', () => {
  it('reports a zero UTC offset and the UTC IANA zone regardless of the host or shell TZ (issue #1292)', () => {
    expect(new Date(2026, 0, 1).getTimezoneOffset()).toBe(0);
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe('UTC');
  });
});

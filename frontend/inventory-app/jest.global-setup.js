/**
 * Jest forks its workers after this runs and they inherit `process.env`, so setting `TZ` here -
 * before any worker starts, and regardless of what the invoking shell set - is what makes every
 * run test dates against a zone that is never the business's own (Australia/Sydney). Without
 * this, Jest runs in the host's zone: UTC in CI, whatever a developer's machine is set to
 * elsewhere, so the same spec can pass on one and fail on the other (issue #1292).
 */
module.exports = async () => {
  process.env.TZ = 'UTC';
};

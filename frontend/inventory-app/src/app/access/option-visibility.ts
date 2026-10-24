/**
 * Which switchable options the application currently offers, as `GET /api/me/access` reported it
 * (issue #873).
 *
 * There is exactly one: `Historical GST Classification`, the occasional Admin maintenance tool of
 * issue #433, which a Super Admin may stop offering to everybody else. The field is named rather
 * than keyed, because a general feature-flag framework is explicitly out of scope of that issue - a
 * second switchable option would be a second named field, decided by its own issue, never an entry
 * in a map of arbitrary keys.
 *
 * **It is not a capability and implies none.** It says what is *offered*; what a member may *do*
 * comes from the capability set beside it, and the endpoints behind the page authorize every request
 * themselves in both states (see docs/architecture.md § Switchable Admin option visibility). A
 * switched-off option therefore hides a page rather than protecting one, exactly as a hidden menu
 * entry always has.
 *
 * `undefined` wherever this type is accepted means the answer is **not known** - nothing has been
 * read, the read failed, or an API that does not report it answered - and then nothing is hidden and
 * no pill is shown. That is the fail-open rule `AccessService` already applies to an unreadable
 * capability set: the two answers travel together, so a missing one means the read that would have
 * said otherwise never answered.
 */
export interface OptionVisibility {
  /** Whether `Historical GST Classification` is offered to anybody but a Super Admin. */
  readonly historicalGstClassificationVisible: boolean;
}

/**
 * Which switchable menu destinations the application currently offers, as `GET /api/me/access`
 * reports it (issues #873, #1148, #1151 and #1153).
 *
 * The answer is a list of **keys that are switched off**, from the closed catalogue the server owns
 * (`Inventory.Application.PlatformOptions.MenuVisibilityCatalogue`). Issue #873 had exactly one
 * switchable option and named it rather than keying it, because a general feature-flag framework was
 * out of scope of that issue; issue #1148 superseded that rule with a **closed, compiled catalogue**
 * and kept the reason behind it - the keys are a fixed list decided in one reviewed place, a caller
 * cannot invent one, and a key the catalogue does not declare is refused with nothing stored. Which
 * menu entries use a key is this application's decision: each entry declares its own key
 * (`layout/navigation.ts`), and several entries may share one.
 *
 * **It is not a capability and implies none.** It says what is *offered*; what a member may *do*
 * comes from the capability set beside it, and the endpoints behind each page authorize every request
 * themselves in both states (see docs/architecture.md § Switchable Admin menu visibility). A
 * switched-off destination therefore hides a page rather than protecting one, exactly as a hidden
 * menu entry always has - and that is as true of the Reports group issue #1153 added as it is of the
 * Admin one: a switched-off report keeps its capability, plan and provider rules exactly as they are.
 *
 * `undefined` wherever this type is accepted means the answer is **not known** - nothing has been
 * read, the read failed, or an API that does not report it answered - and then nothing is hidden and
 * no pill is shown. That is the fail-open rule `AccessService` already applies to an unreadable
 * capability set: the two answers travel together, so a missing one means the read that would have
 * said otherwise never answered.
 */
export interface OptionVisibility {
  /**
   * The catalogue keys that are switched off, and empty when none is. A key this build does not
   * recognise is carried as reported and simply matches no menu entry, so a server that adds a key
   * ahead of the frontend hides nothing unexpectedly.
   */
  readonly switchedOffKeys: readonly string[];
}

/**
 * Every switchable destination's key, in the catalogue's own order - which is the menu's order,
 * Reports above Admin, and the order the Menu Visibility page lists its sections and rows in.
 *
 * It is the frontend's copy of a **server-owned** catalogue, kept so that the declared menu entries
 * can be checked against it at build time (`layout/navigation.spec.ts`): a menu entry declaring a key
 * no catalogue row backs would be a switch nobody could ever operate. The page itself lists the keys,
 * groups and labels the API reports, never this list, so a catalogue the server extends needs no
 * change here for the page to show it.
 */
export const MENU_VISIBILITY_KEYS = [
  'reports.bookkeeping',
  'reports.daily',
  'reports.transactions',
  'reports.reconciliation',
  'reports.machines',
  'reports.products',
  'reports.site-commissions',
  'reports.gst',
  'admin.site-commission-agreements',
  'admin.imports',
  'admin.historical-cost-recovery',
  'admin.avco-transition',
  'admin.costing-repair',
  'admin.historical-gst-classification',
  'admin.nayax-sale-timestamp-repair'
] as const;

/** One of {@link MENU_VISIBILITY_KEYS}, which is the only thing a menu entry may declare. */
export type MenuVisibilityKey = (typeof MENU_VISIBILITY_KEYS)[number];

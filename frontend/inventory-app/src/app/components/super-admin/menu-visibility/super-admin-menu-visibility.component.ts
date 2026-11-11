import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subject, catchError, of, take, takeUntil } from 'rxjs';
import { AccessService } from '../../../access/access.service';
import { entriesWithVisibilityKey } from '../../../layout/navigation';
import { MenuVisibilityOption, MenuVisibilityService } from '../../../services/menu-visibility.service';

/**
 * One switchable destination as this page holds it: the server's own answer, plus the lifecycle of
 * this one row's own save. Nothing here is shared with another row, which is what keeps one refused
 * save away from the rest of the list.
 */
export interface MenuVisibilityRow {
  readonly key: string;
  readonly label: string;

  /** The last state the **server** confirmed for this destination; never a value assumed here. */
  visible: boolean;

  /** True while this row's own save has not answered. */
  saving: boolean;

  /** What this row's last save said when it failed, and `null` while nothing is wrong with it. */
  error: string | null;

  /**
   * The other declared menu entries this one switch also covers (issue #1151), read from
   * `entriesWithVisibilityKey`. Empty for a key one entry declares, which is every key today;
   * the Reimbursement Report of #1152 declares `admin.imports` and appears here with it.
   */
  readonly alsoCovers: readonly string[];
}

/**
 * One menu group as this page lists it (issue #1153): the group's own heading, and the rows under it
 * in the order the server reported them.
 *
 * The grouping is **the server's answer**, not a decision here: the catalogue reports each
 * destination's group and reports them grouped, so the sections are the groups in the order they
 * first appear - `Reports` above `Admin` - and a group the catalogue adds later needs no change here.
 */
export interface MenuVisibilitySection {
  readonly group: string;
  readonly rows: MenuVisibilityRow[];
}

/**
 * The Super Admin **Menu Visibility** page (issues #1151 and #1153): every switchable destination -
 * the business Reports above the Admin maintenance tools - the state each one is in, and the one
 * control that switches it for everybody but a Super Admin.
 *
 * **It is the only place these switches are managed.** Issue #873 put one switch on the Admin hub
 * beside one card; the hub now only renders the result, and the switch for all of them - including
 * Historical GST Classification, whose saved state is unchanged - is here.
 *
 * **The catalogue is the server's answer, not a copy of it.** The keys, their groups, their labels
 * and their order come from `GET /api/super-admin/menu-visibility`, so every declared destination is
 * listed whatever this business's plan, provider or capabilities are: the setting is the platform's,
 * about no business, and this page reads nothing of one. A report locked by this business's plan or
 * its Nayax connection is listed here exactly like any other, because what is being switched is
 * whether the destination is offered at all. Integrations, Users, Roles and Business Settings are
 * never in that catalogue (the owner's decision of 9 November 2026) and so are never listed or
 * hidden here.
 *
 * **Nothing on screen can claim a setting that was not stored.** Each row renders the server's own
 * answer, each control is a button rather than a checkbox - a checkbox carries its own state, which
 * the browser flips on the click, so a refused save would leave it reading as the new setting while
 * the server still holds the old one - and a failed save leaves that row exactly as it was stored and
 * says the change was not saved. The write names one key, so one row's failure leaves the others
 * untouched, and a successful save re-reads `GET /api/me/access`, which is the one answer the
 * sidebar, the bottom nav and the Admin hub build their menus from.
 *
 * **It decides no authority.** Who may read or change these settings is the API's answer - the
 * platform-admin policy on both endpoints - and every switched destination's own endpoints keep
 * authorizing each request by their own capability in both states. A switch hides a page rather than
 * protecting one. The page itself is reached through `platformAdminGuard` like every other
 * `/super-admin/*` page, and is never affected by the switches it manages.
 */
@Component({
  selector: 'app-super-admin-menu-visibility',
  standalone: true,
  templateUrl: './super-admin-menu-visibility.component.html'
})
export class SuperAdminMenuVisibilityComponent implements OnInit, OnDestroy {
  /** Starts empty: no section and no row is shown before the catalogue has been read. */
  sections: MenuVisibilitySection[] = [];

  /** True while the catalogue read has not answered. */
  loading = true;

  /** True when the catalogue read could not answer, so there is nothing to switch from. */
  loadFailed = false;

  private readonly destroying$ = new Subject<void>();

  constructor(
    private readonly service: MenuVisibilityService,
    private readonly access: AccessService
  ) {}

  ngOnInit(): void {
    this.read();
  }

  ngOnDestroy(): void {
    this.destroying$.next();
    this.destroying$.complete();
  }

  /** The administrator asking for the catalogue again after a read of it did not answer. */
  readAgain(): void {
    this.read();
  }

  /** The accessible name of one row's control: the destination, and what the control is about. */
  controlLabel(row: MenuVisibilityRow): string {
    return `${row.label} visibility`;
  }

  /** What the control would do, which is the opposite of what the server last confirmed. */
  controlAction(row: MenuVisibilityRow): string {
    return row.visible ? `Hide ${row.label} from other members` : `Show ${row.label} to other members`;
  }

  /**
   * Switches one destination to the other state - the opposite of the **server's** own answer, never
   * of a value assumed here - and re-reads the shared access answer once the server has stored it.
   */
  apply(row: MenuVisibilityRow): void {
    if (row.saving) {
      return;
    }

    const requested = !row.visible;
    row.saving = true;
    row.error = null;

    this.service
      .set(row.key, requested)
      .pipe(takeUntil(this.destroying$))
      .subscribe({
        next: (stored: MenuVisibilityOption) => {
          row.saving = false;
          // The server's own answer, not the requested value: what is shown is what was stored.
          row.visible = stored.visible;
          // The menus everywhere else are built from one shared read, so that read is taken again.
          this.access
            .refresh()
            .pipe(take(1), takeUntil(this.destroying$))
            .subscribe();
        },
        error: () => {
          row.saving = false;
          // Nothing was stored, so nothing on screen moves: the row still states the last confirmed
          // answer, and says why it is not the one that was asked for.
          row.error = `That change was not saved, so ${row.label} is unchanged. Try again.`;
        }
      });
  }

  private read(): void {
    this.loading = true;
    this.loadFailed = false;

    this.service
      .list()
      .pipe(
        catchError(() => of(null)),
        take(1),
        takeUntil(this.destroying$)
      )
      .subscribe((catalogue) => {
        this.loading = false;
        if (catalogue === null) {
          this.loadFailed = true;
          this.sections = [];
          return;
        }

        this.sections = catalogue.options.reduce<MenuVisibilitySection[]>((sections, option) => {
          const row: MenuVisibilityRow = {
            key: option.key,
            label: option.label,
            visible: option.visible,
            saving: false,
            error: null,
            alsoCovers: entriesWithVisibilityKey(option.key)
              .map((entry) => entry.label)
              .filter((label) => label !== option.label)
          };
          // The server reports the catalogue grouped, so a group is a run of consecutive options and
          // the sections come out in the order it listed them - Reports above Admin.
          const current = sections[sections.length - 1];
          if (current !== undefined && current.group === option.group) {
            current.rows.push(row);
            return sections;
          }

          return [...sections, { group: option.group, rows: [row] }];
        }, []);
      });
  }
}

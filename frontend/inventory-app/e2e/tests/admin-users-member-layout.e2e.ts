import type { Locator, Page, Route } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * The Admin > Users page's Members table at phone width (issue #1274, audit #1225 finding FE-03):
 * at 390px the table was 426px wide, forcing the page to scroll sideways, and the role
 * `<select>` shrank down to its arrow so the member's role could not be read.
 *
 * This is a browser test because jsdom answers none of "does the page scroll sideways at 390px",
 * "does a long email actually wrap onto more than one line" and "is the role control wide enough to
 * read the selected role" - all three need real laid-out boxes and real text metrics against the
 * application's compiled Tailwind CSS. The Jest spec beside the component
 * (`member-management.component.spec.ts`) already asserts the narrow-card markup itself.
 *
 * **Only the member-list `GET` is answered in the browser.** The E2E fixture seeds one membership
 * per business (the signed-in Owner) and never populates its `displayName`/`email`, so there is no
 * way, through the live application, to produce a second member or a long email without a backend
 * fixture or authentication change - which this issue's scope excludes. The synthetic actor, the
 * route guard, the capability check and every other request on the page are the real API; only the
 * list this one check needs is stubbed, with two synthetic members, one of them with a 60+
 * character email.
 *
 * **It writes nothing.** No role is changed, nothing is revoked or reinstated: this only checks
 * that each member's name, email, role, status and action are laid out without sideways scroll at
 * 390px, that the rendered role control measures wide enough for its selected role text, and that
 * the table keeps its existing layout at 1440px.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

/**
 * The width Chromium's own dropdown indicator occupies inside a `<select>`'s content box, in CSS
 * pixels. The indicator is painted inside the content box, so the selected option's label has only
 * `width - border - padding - arrow` left to render in - which is why a control wide enough for the
 * arrow alone (the defect in audit #1225 finding FE-03) still shows no readable role. 16px is a
 * deliberately conservative figure for the pinned Chromium build: the real indicator, with the gap
 * it keeps from the label, is wider, so requiring at least this much makes the assertion lenient
 * about the browser's exact chrome while still rejecting the clipped control.
 */
const nativeSelectArrowWidth = 16;

interface StubbedMember {
  membershipId: number;
  displayName: string;
  email: string;
  role: string;
  isActive: boolean;
  joinedAtUtc: string;
}

/** At least 60 characters, as the acceptance criterion requires. */
const longEmail = 'a.very.long.email.address.belonging.to.one.business.member@example-subdomain.com';

const shortMember: StubbedMember = {
  membershipId: 9001,
  displayName: 'Dana Lee',
  email: 'dana@example.com',
  role: 'Manager',
  isActive: true,
  joinedAtUtc: '2026-01-01T00:00:00Z'
};

const longEmailMember: StubbedMember = {
  membershipId: 9002,
  displayName: 'Long Email Member',
  email: longEmail,
  role: 'Operator',
  isActive: false,
  joinedAtUtc: '2026-01-02T00:00:00Z'
};

/** Answers only `GET /api/members` in the browser; every other request reaches the real API. */
async function stubMemberList(page: Page): Promise<void> {
  await page.route('**/api/members', async (route: Route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([shortMember, longEmailMember])
    });
  });
}

async function openUsersPage(page: Page, viewport: { width: number; height: number }): Promise<void> {
  await stubMemberList(page);
  await page.setViewportSize(viewport);
  await page.goto('/admin/users');
  await expect(page.getByRole('heading', { name: 'Users', level: 1 })).toBeVisible();
  // The card list renders unconditionally - only CSS hides it above `md` - so waiting for both
  // stubbed members' cards is a layout-independent sync point for either viewport.
  await expect(page.locator('[data-testid="member-card"]')).toHaveCount(2);
}

async function pageOverflow(page: Page): Promise<number> {
  return page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
}

/** What a rendered role `<select>` actually measures, and what its selected label needs. */
interface RoleControlMetrics {
  /** The control's laid-out border-box width. */
  readonly width: number;
  readonly left: number;
  readonly right: number;
  /** Border and padding: horizontal space inside the box that no label can use. */
  readonly horizontalChrome: number;
  /** Width the selected option's own label needs, measured in the control's rendered font. */
  readonly selectedLabelWidth: number;
  /** The label that was measured, so a failure names the role that does not fit. */
  readonly selectedLabel: string;
  /** The font the measurement actually used, so the test can refuse a canvas-default fallback. */
  readonly measuredWithFont: string;
  /** The control's own font size, as the browser computed it. */
  readonly fontSize: string;
}

/**
 * Measures the role control as the browser laid it out, together with the width its selected
 * label needs in that same font. Asserting the two against each other is the only way to tell a
 * readable role from the original defect: a control collapsed to its arrow still reports the right
 * `value` and still contains every `<option>`, so value- and option-based assertions pass while
 * nothing legible is on screen.
 */
async function measureRoleControl(select: Locator): Promise<RoleControlMetrics> {
  return select.evaluate((element) => {
    const control = element as HTMLSelectElement;
    const style = getComputedStyle(control);
    const box = control.getBoundingClientRect();
    const selectedLabel = control.options[control.selectedIndex]?.label ?? '';

    // Canvas text metrics, not a character-count estimate: this is the same font stack, weight and
    // size the control is painted with, so the comparison is in real rendered pixels. The
    // shorthand deliberately omits line-height, which does not affect measured width.
    const context = document.createElement('canvas').getContext('2d')!;
    context.font = `${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;

    return {
      width: box.width,
      left: box.x,
      right: box.x + box.width,
      horizontalChrome:
        Number.parseFloat(style.paddingLeft) +
        Number.parseFloat(style.paddingRight) +
        Number.parseFloat(style.borderLeftWidth) +
        Number.parseFloat(style.borderRightWidth),
      selectedLabelWidth: context.measureText(selectedLabel).width,
      selectedLabel,
      measuredWithFont: context.font,
      fontSize: style.fontSize
    };
  });
}

test('at 390x844 the Members list does not scroll sideways, the long email wraps and the role is wide enough to read', async ({
  pageAs
}) => {
  expect(longEmail.length).toBeGreaterThanOrEqual(60);
  const page = await pageAs(actors.businessAOwner);
  await openUsersPage(page, narrowViewport);

  expect(await pageOverflow(page), 'the page does not scroll sideways at 390px').toBeLessThanOrEqual(tolerance);

  // The long email wraps rather than widening the page: its laid-out box is taller than one line
  // of its own text, and stays inside the viewport.
  const longEmailCard = page.locator('[data-testid="member-card"]').filter({ hasText: longEmailMember.displayName });
  const longEmailNode = longEmailCard.locator('[data-testid="member-card-email"]');
  const emailBox = (await longEmailNode.boundingBox())!;
  const lineHeight = await longEmailNode.evaluate((element) => Number.parseFloat(getComputedStyle(element).lineHeight));
  expect(emailBox.height, 'the long email spans more than one line, so it wrapped').toBeGreaterThan(lineHeight * 1.5);
  expect(emailBox.x + emailBox.width, 'the wrapped email stays inside the viewport').toBeLessThanOrEqual(
    narrowViewport.width + tolerance
  );

  // Each member - the short-email active one and the long-email revoked one - shows its name,
  // email, role as readable text, status and its one available action.
  for (const member of [shortMember, longEmailMember]) {
    const card = page.locator('[data-testid="member-card"]').filter({ hasText: member.displayName });
    await expect(card).toBeVisible();
    await expect(card.locator('[data-testid="member-card-email"]')).toHaveText(member.email);

    const roleSelect = card.locator('select');
    await expect(roleSelect).toHaveValue(member.role);
    await expect(roleSelect).toBeVisible();

    // The role is readable, measured rather than inferred: the control, as Chromium laid it out,
    // has room inside its padding and border for the whole selected label *and* the native arrow.
    // This is the assertion that fails on the original defect, where the control was wide enough
    // for its arrow only; `toHaveValue` and an `<option>` count both pass in that state.
    const metrics = await measureRoleControl(roleSelect);
    expect(metrics.selectedLabel, `the control shows ${member.role} as its selected label`).toBe(member.role);
    expect(metrics.measuredWithFont, 'the measurement used the rendered font, not the canvas default').toContain(
      metrics.fontSize
    );
    expect(metrics.selectedLabelWidth, 'the measured label has a real width').toBeGreaterThan(0);

    const widthAvailableForLabel = metrics.width - metrics.horizontalChrome - nativeSelectArrowWidth;
    expect(
      widthAvailableForLabel,
      `the role control is wide enough for "${metrics.selectedLabel}" plus its padding and arrow ` +
        `(rendered ${metrics.width}px, chrome ${metrics.horizontalChrome}px, arrow ${nativeSelectArrowWidth}px, ` +
        `label needs ${metrics.selectedLabelWidth}px)`
    ).toBeGreaterThanOrEqual(metrics.selectedLabelWidth);

    // And the whole control is on screen, so that visible width is not visible only after scrolling.
    expect(metrics.left, 'the role control starts inside the viewport').toBeGreaterThanOrEqual(-tolerance);
    expect(metrics.right, 'the role control ends inside the viewport').toBeLessThanOrEqual(
      narrowViewport.width + tolerance
    );

    if (member.isActive) {
      await expect(card.getByText('Active', { exact: true })).toBeVisible();
      await expect(card.getByRole('button', { name: 'Revoke' })).toBeVisible();
    } else {
      await expect(card.getByText('Revoked', { exact: true })).toBeVisible();
      await expect(card.getByRole('button', { name: 'Reinstate' })).toBeVisible();
    }
  }
});

test('at 1440x900 the Members table keeps its existing layout', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);
  await openUsersPage(page, desktopViewport);

  expect(await pageOverflow(page), 'the page does not scroll sideways at 1440px either').toBeLessThanOrEqual(
    tolerance
  );

  const table = page.locator('[data-testid="members-table"]');
  await expect(table).toBeVisible();
  await expect(table.locator('table.table')).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'Name' })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'Email' })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'Role' })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'Status' })).toBeVisible();
  await expect(table.getByRole('columnheader', { name: 'Actions' })).toBeVisible();

  const row = table.locator('[data-testid="member-row"]').filter({ hasText: shortMember.displayName });
  await expect(row).toBeVisible();
  await expect(row.locator('select')).toHaveValue(shortMember.role);
  await expect(row.getByRole('button', { name: 'Revoke' })).toBeVisible();

  // The narrow-width card list exists in the DOM but is not what is shown at this width.
  await expect(page.locator('[data-testid="member-cards"]')).toBeHidden();
});

import type { Page, Route } from '@playwright/test';
import { actors, expect, test } from '../fixtures/harness';

/**
 * The Admin > Users page's Members table at phone width (issue #1274, audit #1225 finding FE-03):
 * at 390px the table was 426px wide, forcing the page to scroll sideways, and the role
 * `<select>` shrank down to its arrow so the member's role could not be read.
 *
 * This is a browser test because jsdom answers neither "does the page scroll sideways at 390px"
 * nor "does a long email actually wrap onto more than one line" - both need real laid-out boxes
 * against the application's compiled Tailwind CSS. The Jest spec beside the component
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
 * 390px and that the table keeps its existing layout at 1440px.
 */
const desktopViewport = { width: 1440, height: 900 };
const narrowViewport = { width: 390, height: 844 };

/** One CSS pixel of slack: a laid-out box edge is fractional, a viewport bound is not. */
const tolerance = 1;

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

test('at 390x844 the Members list does not scroll sideways and the long email wraps', async ({ pageAs }) => {
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
    // The chosen option's own label is on screen - not merely a value - so the role is readable
    // text, not a control shrunk down to its arrow.
    await expect(roleSelect.locator('option', { hasText: member.role })).toHaveCount(1);

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

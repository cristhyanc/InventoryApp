import { actors, expect, seeded, test } from '../fixtures/harness';

/**
 * The reporting invariant with the most expensive failure mode (issue #46, AGENTS.md § Profit
 * calculations): when a completed sale has no cost of goods, profit is *unavailable*. It must never
 * be rendered as zero, because a zero reads as "this sale made no profit" instead of "this cannot
 * be calculated yet", and an operator cannot tell the difference.
 *
 * The fixture seeds exactly that state: one completed card sale of $5.50 with no persisted cost and
 * no Nayax transaction cost price. The report therefore has real sales and no COGS, which is the
 * only situation where "unavailable, not zero" can be told apart from an empty report.
 */
test('a report with an uncosted sale shows profit as unavailable, never as zero', async ({ pageAs }) => {
  const page = await pageAs(actors.businessAOwner);

  await page.goto('/reports');
  await page.getByRole('button', { name: 'Custom' }).click();
  // Exact names: the shell's account-menu button also contains the substring "To".
  await page.getByRole('textbox', { name: 'From', exact: true }).fill(seeded.uncostedSaleDate);
  await page.getByRole('textbox', { name: 'To', exact: true }).fill(seeded.uncostedSaleDate);
  await page.getByRole('button', { name: 'Apply', exact: true }).click();

  // Sales are present, so this is not an empty report.
  await expect(page.getByTestId('dashboard-total-sales')).toHaveText('$5.50');

  // Gross profit cannot be calculated, and says so.
  await expect(page.getByTestId('dashboard-gross-profit')).toHaveText('Profit unavailable');
  await expect(page.getByTestId('dashboard-gross-profit')).not.toHaveText('$0.00');
  await expect(page.getByTestId('dashboard-gross-margin')).toHaveText('COGS incomplete');

  // Net profit is unavailable for the same reason, and is not reported as a loss of zero either.
  await expect(page.getByTestId('dashboard-net-profit')).toHaveText('Profit unavailable');

  // The COGS that is known is presented as partial, not as a complete zero.
  await expect(page.getByTestId('dashboard-cost-of-goods')).toContainText('Partial');
});

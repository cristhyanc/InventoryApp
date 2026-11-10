import { OperatingExpenseCategory } from '../../services/operating-expense.service';

/**
 * The expense categories and their labels, shared by the register and the Add/Edit expense form
 * (issue #1167). Both screens name the same categories in the same order and spell them the same
 * way, and before the form moved onto its own pages that wording lived on the register component;
 * it is here so the two pages cannot drift apart.
 */
export const OPERATING_EXPENSE_CATEGORIES = Object.values(OperatingExpenseCategory).filter(
  (value) => typeof value === 'number'
) as OperatingExpenseCategory[];

/** The enum member names, in declaration order, as the labels are derived from. */
const CATEGORY_NAMES = [
  'NayaxMonthlyFee',
  'Insurance',
  'RepairsAndMaintenance',
  'Software',
  'Accounting',
  'PhoneInternet',
  'VehicleTravel',
  'BankFees',
  'Other'
];

/** A category's label: its name split at each word boundary, "Other" for anything unrecognised. */
export function expenseCategoryLabel(category: OperatingExpenseCategory): string {
  return CATEGORY_NAMES[Number(category)]?.replace(/([a-z])([A-Z])/g, '$1 $2') ?? 'Other';
}

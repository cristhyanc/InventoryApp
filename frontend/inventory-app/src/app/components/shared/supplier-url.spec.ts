import * as sharedLocation from './supplier-url';
import { isSafeSupplierUrl, normalizeSupplierUrl } from '../suppliers/supplier-url';

/**
 * The one supplier website rule lives in `components/suppliers/supplier-url.ts` (issue #847), and
 * this folder holds no second copy of it (the reconciliation of #848 with #847). Both files are
 * intended to be deleted - see the note in `./supplier-url.ts` - and until then this keeps the
 * emptied module from quietly growing a rule of its own again.
 */
describe('the supplier website rule has one home', () => {
  it('exports nothing from this folder and keeps the rule beside the suppliers feature', () => {
    expect(Object.keys(sharedLocation)).toEqual([]);
    expect(typeof normalizeSupplierUrl).toBe('function');
    expect(typeof isSafeSupplierUrl).toBe('function');
  });
});

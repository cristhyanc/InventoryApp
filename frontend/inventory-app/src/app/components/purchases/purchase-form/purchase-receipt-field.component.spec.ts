import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  FakeImageDecoding,
  fileOfSize,
  installFakeImageDecoding
} from '../../shared/image-upload-preparation.spec-helpers';
import { PurchaseReceiptFieldComponent } from './purchase-receipt-field.component';

describe('PurchaseReceiptFieldComponent (issue #1029)', () => {
  let fixture: ComponentFixture<PurchaseReceiptFieldComponent>;
  let component: PurchaseReceiptFieldComponent;
  let host: HTMLElement;
  let changes: (File | null)[];
  let previews: number;
  let decoding: FakeImageDecoding | null = null;

  afterEach(() => {
    decoding?.restore();
    decoding = null;
  });

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [PurchaseReceiptFieldComponent] }).compileComponents();
    fixture = TestBed.createComponent(PurchaseReceiptFieldComponent);
    component = fixture.componentInstance;
    host = fixture.nativeElement as HTMLElement;
    changes = [];
    previews = 0;
    component.pendingFileChange.subscribe((file) => changes.push(file));
    component.previewStored.subscribe(() => previews++);
    fixture.detectChanges();
  });

  const byTestId = (id: string) => host.querySelector<HTMLElement>(`[data-testid="${id}"]`);

  it('offers only a compact Attach receipt button when there is no receipt', () => {
    expect(byTestId('purchase-receipt-attach')?.textContent).toContain('Attach receipt');
    expect(byTestId('purchase-receipt-preview')).toBeNull();
    expect((byTestId('purchase-receipt-input') as HTMLInputElement).accept).toBe('image/*,.pdf');
  });

  it('opens the file chooser from Attach receipt and reports the chosen file', async () => {
    const input = byTestId('purchase-receipt-input') as HTMLInputElement;
    const click = jest.spyOn(input, 'click').mockImplementation(() => undefined);
    byTestId('purchase-receipt-attach')!.click();
    expect(click).toHaveBeenCalled();

    const file = new File(['scan'], 'receipt.pdf', { type: 'application/pdf' });
    await component.onFileSelected({ target: { files: [file], value: 'C:\\fakepath\\receipt.pdf' } } as unknown as Event);

    expect(changes).toEqual([file]);
  });

  /**
   * Issue #1161: the photo is shrunk before it is reported, so the size this control shows, the
   * bytes the purchase uploads and what counts against the business's storage allowance are one
   * and the same file.
   */
  it('shrinks a phone photo before reporting it, and shows the size that will be uploaded', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_560 });
    // What the page does with the reported file: it becomes the pending receipt this control shows.
    component.pendingFileChange.subscribe((file) => fixture.componentRef.setInput('pendingFile', file));

    const photo = fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg');
    await component.onFileSelected({ target: { files: [photo], value: '' } } as unknown as Event);
    fixture.detectChanges();

    expect(changes).toHaveLength(1);
    expect(changes[0]?.size).toBe(450_560);
    expect(byTestId('purchase-receipt-name')?.textContent).toBe('IMG_4312.jpg');
    expect(host.textContent).toContain('440 KB');
  });

  it('converts a HEIC photo the browser can decode into a .jpg', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 3024, height: 4032 }, encodedBytes: 400_000 });

    await component.onFileSelected(
      { target: { files: [fileOfSize(3_900_000, 'IMG_0007.HEIC')], value: '' } } as unknown as Event);

    expect(changes[0]?.name).toBe('IMG_0007.jpg');
    expect(changes[0]?.type).toBe('image/jpeg');
  });

  it('says it is preparing the photo while it is being shrunk, and reports that to the form', async () => {
    decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_560 });
    const preparing: boolean[] = [];
    component.preparingChange.subscribe((value) => preparing.push(value));

    const selection = component.onFileSelected(
      { target: { files: [fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg')], value: '' } } as unknown as Event);
    fixture.detectChanges();
    expect(byTestId('purchase-receipt-preparing')).not.toBeNull();
    expect(component.preparing).toBe(true);

    await selection;
    fixture.detectChanges();

    expect(component.preparing).toBe(false);
    expect(byTestId('purchase-receipt-preparing')).toBeNull();
    expect(preparing).toEqual([true, false]);
  });

  it('shows a chosen file with Preview, Replace and Remove, and hides Attach', () => {
    fixture.componentRef.setInput('pendingFile', new File(['x'.repeat(2048)], 'receipt.jpg', { type: 'image/jpeg' }));
    fixture.detectChanges();

    expect(byTestId('purchase-receipt-name')?.textContent).toBe('receipt.jpg');
    expect(host.textContent).toContain('2 KB');
    expect(byTestId('purchase-receipt-preview')).not.toBeNull();
    expect(byTestId('purchase-receipt-replace')).not.toBeNull();
    expect(byTestId('purchase-receipt-remove')).not.toBeNull();
    expect(byTestId('purchase-receipt-attach')).toBeNull();
  });

  it('removes a chosen file without touching the server', () => {
    fixture.componentRef.setInput('pendingFile', new File(['x'], 'receipt.jpg'));
    fixture.detectChanges();

    byTestId('purchase-receipt-remove')!.click();

    expect(changes).toEqual([null]);
  });

  it('previews a chosen file locally in a new tab', () => {
    const open = jest.spyOn(window, 'open').mockImplementation(() => null);
    Object.defineProperty(URL, 'createObjectURL', { value: jest.fn(() => 'blob:local'), configurable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: jest.fn(), configurable: true });
    fixture.componentRef.setInput('pendingFile', new File(['x'], 'receipt.jpg'));
    fixture.detectChanges();

    byTestId('purchase-receipt-preview')!.click();

    expect(open).toHaveBeenCalledWith('blob:local', '_blank', 'noopener');
    fixture.destroy();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:local');
    open.mockRestore();
  });

  it('previews a saved receipt but offers no replace or remove for it, so nothing stored is deleted', () => {
    fixture.componentRef.setInput('stored', { fileName: 'tax-invoice.pdf', fileSizeBytes: 1468006 });
    fixture.detectChanges();

    expect(byTestId('purchase-receipt-name')?.textContent).toBe('tax-invoice.pdf');
    expect(host.textContent).toContain('1.4 MB');
    expect(byTestId('purchase-receipt-replace')).toBeNull();
    expect(byTestId('purchase-receipt-remove')).toBeNull();
    expect(byTestId('purchase-receipt-attach')).toBeNull();
    expect(byTestId('purchase-receipt-stored-note')).not.toBeNull();

    byTestId('purchase-receipt-preview')!.click();
    expect(previews).toBe(1);
  });

  it('formats sizes as a person reads them', () => {
    expect(component.formatSize(512)).toBe('512 B');
    expect(component.formatSize(350_000)).toBe('342 KB');
    expect(component.formatSize(null)).toBe('');
  });
});

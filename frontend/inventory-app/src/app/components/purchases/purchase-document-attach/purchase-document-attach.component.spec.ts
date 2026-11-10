import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, of, throwError } from 'rxjs';
import { GstClassification, GstClassificationSource, Purchase } from '../../../models/models';
import { PurchaseService } from '../../../services/purchase.service';
import { ToastService } from '../../../services/toast.service';
import {
  FakeImageDecoding,
  fileOfSize,
  installFakeImageDecoding
} from '../../shared/image-upload-preparation.spec-helpers';
import { ATTACH_FAILED_MESSAGE, PurchaseDocumentAttachComponent } from './purchase-document-attach.component';

/** A purchase recorded with no supporting document at all (issue #976). */
function purchase(): Purchase {
  return {
    id: 7,
    title: 'Weekly restock',
    notes: null,
    totalAmount: 10,
    deliveryCost: null,
    deliveryGstClassification: GstClassification.Unknown,
    deliveryGstClassificationSource: GstClassificationSource.Unknown,
    packageCost: null,
    packageGstClassification: GstClassification.Unknown,
    packageGstClassificationSource: GstClassificationSource.Unknown,
    purchaseDate: '2026-04-01T00:00:00Z',
    supplierId: null,
    supplier: null,
    fileName: null,
    storedFileName: null,
    contentType: null,
    fileSizeBytes: null,
    createdAt: '2026-04-01T00:00:00Z',
    items: []
  };
}

async function render(answer: Observable<Purchase> = of(purchase())) {
  const attachDocument = jest.fn((_purchaseId: number, _file: File) => answer);
  const success = jest.fn();
  await TestBed.configureTestingModule({
    imports: [PurchaseDocumentAttachComponent],
    providers: [
      { provide: PurchaseService, useValue: { attachDocument } },
      { provide: ToastService, useValue: { success, error: jest.fn(), warning: jest.fn(), info: jest.fn() } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(PurchaseDocumentAttachComponent);
  fixture.componentRef.setInput('purchase', purchase());
  const attached = jest.fn();
  fixture.componentInstance.attached.subscribe(attached);
  fixture.detectChanges();

  return {
    fixture,
    component: fixture.componentInstance,
    host: fixture.nativeElement as HTMLElement,
    attachDocument,
    attached,
    success
  };
}

/** The change event the file input raises, with one chosen file. */
function choose(component: PurchaseDocumentAttachComponent, file: File): Promise<void> {
  return component.onFileSelected({ target: { files: [file] } } as unknown as Event);
}

afterEach(() => TestBed.resetTestingModule());

describe('PurchaseDocumentAttachComponent (issue #976)', () => {
  it('sends the chosen document for that purchase and reports it was added', async () => {
    const { component, attachDocument, attached, success } = await render();
    const file = new File(['content'], 'invoice.pdf', { type: 'application/pdf' });
    await choose(component, file);

    component.attach();

    expect(attachDocument).toHaveBeenCalledWith(7, file);
    expect(attached).toHaveBeenCalledTimes(1);
    expect(success).toHaveBeenCalledWith('Document added.');
    expect(component.selectedFile).toBeNull();
    expect(component.saving).toBe(false);
    expect(component.error).toBe('');
  });

  it('sends nothing until a document has been chosen', async () => {
    const { component, attachDocument, attached } = await render();

    component.attach();

    expect(attachDocument).not.toHaveBeenCalled();
    expect(attached).not.toHaveBeenCalled();
  });

  /**
   * The server's own sentence, whichever refusal it was: the size and type rules, and the refusal to
   * replace a document that arrived in the meantime, are all worth reading as they are rather than
   * being flattened into one house message.
   */
  it("shows the API's own refusal and raises nothing", async () => {
    const refusal = 'This purchase already has a document, so another one cannot be attached.';
    const { component, attached } = await render(
      throwError(() => new HttpErrorResponse({ status: 409, error: refusal }))
    );
    await choose(component, new File(['content'], 'invoice.pdf', { type: 'application/pdf' }));

    component.attach();

    expect(component.error).toBe(refusal);
    expect(component.saving).toBe(false);
    expect(attached).not.toHaveBeenCalled();
  });

  it('falls back to its own message when the failure carries no sentence', async () => {
    const { component } = await render(throwError(() => new HttpErrorResponse({ status: 500 })));
    await choose(component, new File(['content'], 'invoice.pdf', { type: 'application/pdf' }));

    component.attach();

    expect(component.error).toBe(ATTACH_FAILED_MESSAGE);
  });

  it('gives the file input an accessible name that says which purchase it is for', async () => {
    const { host } = await render();

    const input = host.querySelector('input[type="file"]');
    expect(input?.getAttribute('aria-label')).toBe('Add a document to Weekly restock');
    expect(host.querySelector('label')?.getAttribute('for')).toBe(input?.getAttribute('id'));
  });

  it('offers the upload control only once a document has been chosen', async () => {
    const { fixture, component, host } = await render();

    expect(host.querySelector('[data-testid="purchase-document-attach-upload"]')).toBeNull();

    await choose(component, new File(['content'], 'invoice.pdf', { type: 'application/pdf' }));
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="purchase-document-attach-upload"]')).not.toBeNull();
  });

  /** Issue #1161: the same shared helper every other attachment upload uses. */
  describe('shrinking a photo before it is sent (issue #1161)', () => {
    let decoding: FakeImageDecoding | null = null;

    afterEach(() => {
      decoding?.restore();
      decoding = null;
    });

    it('sends the shrunk photo rather than the 5 MB one that was chosen', async () => {
      decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
      const { component, attachDocument } = await render();

      await choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
      component.attach();

      expect(attachDocument).toHaveBeenCalledTimes(1);
      const sent = attachDocument.mock.calls[0][1] as File;
      expect(sent.size).toBe(450_000);
      expect(sent.type).toBe('image/jpeg');
    });

    it('converts a HEIC photo the browser can decode into a .jpg', async () => {
      decoding = installFakeImageDecoding({ decoded: { width: 3024, height: 4032 }, encodedBytes: 400_000 });
      const { component, attachDocument } = await render();

      await choose(component, fileOfSize(3_900_000, 'IMG_0007.heic', 'image/heic'));
      component.attach();

      expect((attachDocument.mock.calls[0][1] as File).name).toBe('IMG_0007.jpg');
    });

    it('sends the original HEIC when the browser cannot decode it, and shows no error of its own', async () => {
      decoding = installFakeImageDecoding({ decoded: 'undecodable' });
      const { component, attachDocument } = await render();
      const heic = fileOfSize(3_400_000, 'IMG_0008.heic', 'image/heic');

      await choose(component, heic);
      component.attach();

      expect(attachDocument).toHaveBeenCalledWith(7, heic);
      expect(component.error).toBe('');
    });

    it('adds nothing while the photo is still being prepared', async () => {
      decoding = installFakeImageDecoding({ decoded: { width: 4000, height: 3000 }, encodedBytes: 450_000 });
      const { fixture, component, host, attachDocument } = await render();

      const selection = choose(component, fileOfSize(5 * 1024 * 1024, 'IMG_4312.jpg', 'image/jpeg'));
      fixture.detectChanges();

      expect(component.preparing).toBe(true);
      expect(host.querySelector('[data-testid="purchase-document-attach-preparing"]')).not.toBeNull();
      component.attach();
      expect(attachDocument).not.toHaveBeenCalled();

      await selection;
      fixture.detectChanges();

      expect(component.preparing).toBe(false);
      expect(host.querySelector('[data-testid="purchase-document-attach-preparing"]')).toBeNull();
      component.attach();
      expect(attachDocument).toHaveBeenCalledTimes(1);
    });
  });
});

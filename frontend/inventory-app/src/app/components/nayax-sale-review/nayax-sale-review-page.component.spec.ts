import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of, throwError } from 'rxjs';
import { AccessService } from '../../access/access.service';
import { BusinessTimeZoneService } from '../../formatting/business-time-zone.service';
import {
  NayaxSaleDecisionApplied,
  NayaxSaleDecisionBody,
  NayaxSaleEvidenceKind,
  NayaxSaleFieldComparison,
  NayaxSaleOwnerDecision,
  NayaxSaleReviewEvidence,
  NayaxSaleReviewItem,
  NayaxSaleReviewPage,
  NayaxSaleReviewService,
  NayaxSaleReviewView,
  NayaxSqsVerification
} from '../../services/nayax-sale-review.service';
import { NayaxSaleReviewPageComponent } from './nayax-sale-review-page.component';

function comparison(overrides: Partial<NayaxSaleReviewEvidence> = {}): NayaxSaleReviewEvidence {
  return {
    id: 7,
    kind: NayaxSaleEvidenceKind.Comparison,
    observedAtUtc: '2026-11-06T01:00:00Z',
    resultingState: NayaxSqsVerification.Conflict,
    providerMachineId: 9001,
    providerSettlementValue: 4.5,
    providerAuthorizationInstantUtc: '2026-11-06T00:30:00Z',
    providerCurrencyCode: 'AUD',
    providerNayaxProductId: 55,
    providerProductName: 'Cola 600ml',
    machineComparison: NayaxSaleFieldComparison.Match,
    amountComparison: NayaxSaleFieldComparison.Mismatch,
    instantComparison: NayaxSaleFieldComparison.Match,
    productComparison: NayaxSaleFieldComparison.Match,
    readMachineId: null,
    returnedItemCount: null,
    returnedEarliestInstantUtc: null,
    returnedLatestInstantUtc: null,
    ...overrides
  };
}

function absence(): NayaxSaleReviewEvidence {
  return comparison({
    id: 8,
    kind: NayaxSaleEvidenceKind.Absence,
    resultingState: NayaxSqsVerification.NeedsInvestigation,
    providerMachineId: null,
    providerSettlementValue: null,
    providerAuthorizationInstantUtc: null,
    providerNayaxProductId: null,
    providerProductName: null,
    machineComparison: null,
    amountComparison: null,
    instantComparison: null,
    productComparison: null,
    readMachineId: 9001,
    returnedItemCount: 12,
    returnedEarliestInstantUtc: '2026-11-05T22:00:00Z',
    returnedLatestInstantUtc: '2026-11-06T02:00:00Z'
  });
}

function conflictItem(overrides: Partial<NayaxSaleReviewItem> = {}): NayaxSaleReviewItem {
  return {
    saleId: 101,
    transactionId: 5550001,
    machineId: 9001,
    machineName: 'Avenue Hotel',
    settlementValue: 5,
    authorizationInstantUtc: '2026-11-06T00:30:00Z',
    receivedViaSqsAtUtc: '2026-11-06T00:30:05Z',
    state: NayaxSqsVerification.Conflict,
    isCounted: false,
    nayaxProductId: 55,
    productId: 3,
    productName: 'Cola 600ml',
    latestEvidenceId: 7,
    latestComparison: comparison(),
    latestAbsence: null,
    history: [],
    availableDecisions: [
      { decision: NayaxSaleOwnerDecision.UseNayaxValues, evidenceId: 7 },
      { decision: NayaxSaleOwnerDecision.KeepSqsValues, evidenceId: 7 },
      { decision: NayaxSaleOwnerDecision.DoNotCount, evidenceId: 7 }
    ],
    ...overrides
  };
}

function investigationItem(): NayaxSaleReviewItem {
  return conflictItem({
    saleId: 102,
    transactionId: 5550002,
    state: NayaxSqsVerification.NeedsInvestigation,
    isCounted: true,
    latestEvidenceId: 8,
    latestComparison: null,
    latestAbsence: absence(),
    availableDecisions: [
      { decision: NayaxSaleOwnerDecision.DoNotCount, evidenceId: 8 },
      { decision: NayaxSaleOwnerDecision.Genuine, evidenceId: 8 }
    ]
  });
}

interface RenderOptions {
  items?: NayaxSaleReviewItem[];
  list?: (view: NayaxSaleReviewView, pageIndex: number, pageSize: number) => Observable<NayaxSaleReviewPage>;
  canDecide?: boolean;
  decide?: (saleId: number, body: NayaxSaleDecisionBody) => Observable<NayaxSaleDecisionApplied>;
}

async function render(options: RenderOptions = {}) {
  const list = jest.fn(
    options.list
      ?? ((_view: NayaxSaleReviewView, pageIndex: number, pageSize: number) => {
        const items = options.items ?? [conflictItem(), investigationItem()];
        return of({ items, totalCount: items.length, pageIndex, pageSize });
      })
  );
  const decide = jest.fn(
    options.decide ?? (() => of({ saleId: 101, resultingState: NayaxSqsVerification.OwnerCorrected, auditId: 1 }))
  );

  await TestBed.configureTestingModule({
    imports: [NayaxSaleReviewPageComponent],
    providers: [
      provideRouter([]),
      { provide: NayaxSaleReviewService, useValue: { list, decide } },
      { provide: AccessService, useValue: { allows: () => of(options.canDecide ?? true) } },
      { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(NayaxSaleReviewPageComponent);
  fixture.detectChanges();
  const host = fixture.nativeElement as HTMLElement;
  const click = (selector: string) => {
    (host.querySelector(selector) as HTMLElement).click();
    fixture.detectChanges();
  };
  return { fixture, host, list, decide, click };
}

const item = (host: HTMLElement, saleId: number) =>
  host.querySelector(`[data-testid="nayax-sale-review-item-${saleId}"]`) as HTMLElement;

describe('NayaxSaleReviewPageComponent (issue #1105)', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('lists each sale with its state, its stored values and Nayax evidence', async () => {
    const { host } = await render();

    const conflict = item(host, 101);
    expect(conflict.textContent).toContain('Transaction 5550001');
    expect(conflict.querySelector('[data-testid="nayax-sale-review-state"]')?.textContent).toContain(
      'Contradicted by Nayax - not counted'
    );
    expect(conflict.querySelector('[data-testid="nayax-sale-review-comparison"]')?.textContent).toContain('differs');
    expect(conflict.textContent).toContain('Counted in sales: No');
  });

  it('describes an absence as what lastSales returned for the machine, never as missing or fake', async () => {
    const { host } = await render();

    const text = item(host, 102).querySelector('[data-testid="nayax-sale-review-absence"]')?.textContent ?? '';
    expect(text).toContain('12 sales for machine 9001');
    expect(text).toContain('06/11/2026');
    expect(text.toLowerCase()).not.toContain('missing from nayax');
    expect(text.toLowerCase()).not.toContain('fake');
  });

  it('offers an Owner exactly the decisions the server allows for each sale', async () => {
    const { host } = await render();

    const offered = (saleId: number) =>
      Array.from(item(host, saleId).querySelectorAll('[data-testid^="nayax-sale-review-decision-"]')).map((button) =>
        button.textContent?.trim()
      );
    expect(offered(101)).toEqual(["Use Nayax's values", 'Keep the SQS values', "Don't count this sale"]);
    expect(offered(102)).toEqual(["Don't count this sale", 'This sale is genuine']);
  });

  it('offers no decision to a member who is not an Owner', async () => {
    const { host } = await render({ canDecide: false });

    expect(host.querySelector('[data-testid="nayax-sale-review-decisions"]')).toBeNull();
    expect(host.querySelector('[data-testid="nayax-sale-review-owner-only"]')).not.toBeNull();
  });

  it("sends Use Nayax's values with the presented state, latest evidence and relied-on evidence, and no reason", async () => {
    const { click, decide, list } = await render();

    click(`[data-testid="nayax-sale-review-item-101"] [data-testid="nayax-sale-review-decision-${NayaxSaleOwnerDecision.UseNayaxValues}"]`);
    click('[data-testid="nayax-sale-review-submit"]');

    expect(decide).toHaveBeenCalledWith(101, {
      presentedState: NayaxSqsVerification.Conflict,
      presentedLatestEvidenceId: 7,
      evidenceId: 7,
      decision: NayaxSaleOwnerDecision.UseNayaxValues,
      reason: null
    });
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('requires a reason before a decision that needs one can be sent', async () => {
    const { click, decide, fixture, host } = await render();

    click(`[data-testid="nayax-sale-review-item-102"] [data-testid="nayax-sale-review-decision-${NayaxSaleOwnerDecision.Genuine}"]`);
    const submit = host.querySelector('[data-testid="nayax-sale-review-submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    fixture.componentInstance.pending!.reason = '  Seen on the machine camera  ';
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    click('[data-testid="nayax-sale-review-submit"]');

    expect(decide).toHaveBeenCalledWith(102, expect.objectContaining({
      decision: NayaxSaleOwnerDecision.Genuine,
      evidenceId: 8,
      reason: 'Seen on the machine camera'
    }));
  });

  it("shows the server's 409 sentence and reloads the list so the Owner decides against what is true now", async () => {
    const message = "Nayax's product isn't linked to a product in this business. Link it first, or choose another decision.";
    const { click, host, list } = await render({
      decide: () =>
        throwError(() => new HttpErrorResponse({ status: 409, error: { title: 'Conflict', message } }))
    });

    click(`[data-testid="nayax-sale-review-item-101"] [data-testid="nayax-sale-review-decision-${NayaxSaleOwnerDecision.UseNayaxValues}"]`);
    click('[data-testid="nayax-sale-review-submit"]');

    expect(host.querySelector('[data-testid="nayax-sale-review-error"]')?.textContent).toContain(message);
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('shows Owner decisions as confirmed by owner, never as verified by Nayax, with the history', async () => {
    const { host } = await render({
      items: [
        conflictItem({
          state: NayaxSqsVerification.OwnerCorrected,
          isCounted: true,
          availableDecisions: [],
          history: [
            {
              id: 1,
              superseded: false,
              decision: NayaxSaleOwnerDecision.UseNayaxValues,
              evidenceId: 7,
              reason: null,
              occurredAtUtc: '2026-11-06T03:00:00Z',
              previousState: NayaxSqsVerification.Conflict,
              resultingState: NayaxSqsVerification.OwnerCorrected,
              reconciledPeriodFrom: '2026-11-01T00:00:00',
              reconciledPeriodTo: '2026-11-07T00:00:00',
              reconciledPeriodDifference: 4.5
            }
          ]
        })
      ]
    });

    const sale = item(host, 101);
    expect(sale.querySelector('[data-testid="nayax-sale-review-state"]')?.textContent?.trim()).toBe('Confirmed by owner');
    expect(sale.textContent).not.toContain('Verified by Nayax');
    const history = sale.querySelector('[data-testid="nayax-sale-review-history"]')?.textContent ?? '';
    expect(history).toContain("Use Nayax's values by the owner");
    expect(history).toContain('Changed the reimbursement period 01/11/2026');
  });

  it('says so when nothing needs review, and when the list could not be loaded', async () => {
    const empty = await render({ items: [] });
    expect(empty.host.querySelector('[data-testid="nayax-sale-review-empty"]')).not.toBeNull();
    TestBed.resetTestingModule();

    await TestBed.configureTestingModule({
      imports: [NayaxSaleReviewPageComponent],
      providers: [
        provideRouter([]),
        { provide: NayaxSaleReviewService, useValue: { list: () => throwError(() => new Error('offline')) } },
        { provide: AccessService, useValue: { allows: () => of(true) } },
        { provide: BusinessTimeZoneService, useValue: { timeZoneId: 'Australia/Sydney' } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(NayaxSaleReviewPageComponent);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="nayax-sale-review-failed"]')).not.toBeNull();
  });

  it('reads the needs-review list first, one server page at a time', async () => {
    const { list } = await render();

    expect(list).toHaveBeenCalledTimes(1);
    expect(list).toHaveBeenCalledWith(NayaxSaleReviewView.NeedsReview, 0, 25);
  });

  it('reaches every flagged sale by paging, however many there are', async () => {
    const page = (pageIndex: number): NayaxSaleReviewPage => ({
      items: [conflictItem({ saleId: 200 + pageIndex, transactionId: 6660000 + pageIndex })],
      totalCount: 60,
      pageIndex,
      pageSize: 25
    });
    const { click, host, list } = await render({ list: (_view, pageIndex) => of(page(pageIndex)) });

    expect(host.querySelector('[data-testid="list-pagination-range"]')?.textContent).toContain('60');
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toContain('Page 1 of 3');
    click('[data-testid="list-pagination-next"]');
    click('[data-testid="list-pagination-next"]');

    expect(list).toHaveBeenLastCalledWith(NayaxSaleReviewView.NeedsReview, 2, 25);
    expect(item(host, 202)).not.toBeNull();
    expect(host.querySelector('[data-testid="list-pagination-page"]')?.textContent).toContain('Page 3 of 3');
  });

  it('switches to the decided list from its first page, and back', async () => {
    const { click, list } = await render();

    click('[data-testid="nayax-sale-review-view-decided"]');
    expect(list).toHaveBeenLastCalledWith(NayaxSaleReviewView.Decided, 0, 25);

    click('[data-testid="nayax-sale-review-view-needs-review"]');
    expect(list).toHaveBeenLastCalledWith(NayaxSaleReviewView.NeedsReview, 0, 25);
  });

  it('steps back a page when a decision emptied the last one', async () => {
    const { fixture, list } = await render({
      list: (_view, pageIndex) =>
        of({ items: pageIndex === 0 ? [conflictItem()] : [], totalCount: pageIndex === 0 ? 26 : 25, pageIndex, pageSize: 25 })
    });

    fixture.componentInstance.onPageIndexChange(1);
    fixture.detectChanges();

    expect(list).toHaveBeenLastCalledWith(NayaxSaleReviewView.NeedsReview, 0, 25);
    expect(fixture.componentInstance.pageIndex).toBe(0);
  });
});

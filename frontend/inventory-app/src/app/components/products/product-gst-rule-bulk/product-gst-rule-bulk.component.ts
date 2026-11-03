import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  Output,
  Renderer2,
  SimpleChanges,
  ViewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { ProductService } from '../../../services/product.service';
import { GstClassification } from '../../../models/models';

/** Which question the dialog is asking: the rule, or the confirmation of the change it implies. */
type BulkGstRuleStep = 'choose' | 'confirm';

/**
 * Reads the sentence a refusal carries. The bulk GST rule endpoints answer a `ProblemDetails` - the
 * shape `DomainValidationException`/`DomainConflictException` are mapped to centrally - so the text
 * is in `message`, not in a raw string body like the products endpoints' own refusals
 * (`api-error-message.ts`). `title` is the fallback the invite page reads for the same reason.
 */
function refusalMessage(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { message?: unknown; title?: unknown } | null;
    const message = body?.message ?? body?.title;
    if (typeof message === 'string' && message.trim().length > 0) {
      return message;
    }
  }

  return fallback;
}

/**
 * "Set GST rule in bulk" (issue #1076): the dialog that configures one GST rule on every product of
 * the business that currently has none.
 *
 * <p>It is a dedicated feature component rather than markup grown inside `ProductListComponent`,
 * per the page composition boundary (docs/architecture.md § Page composition boundary): it is a
 * distinct workflow with its own substantial UI, its own two-step state and its own
 * loading/error/saving lifecycle, and the page composes it through one input and two outputs. The
 * automated `page-composition.guard.ts` check is the same rule written down.</p>
 *
 * <p><strong>It decides no tax treatment and counts nothing.</strong> The eligible count is the
 * server's own answer, read when the dialog opens and re-read after a conflict; the rule is the
 * operator's choice, and there is deliberately no default - "the owner chooses Taxable or GST-free
 * at use time" is this issue's own rule, and a preselected treatment would be this application
 * guessing at one. The changed count the page reports is the one the committed write returned, never
 * the count that was confirmed.</p>
 *
 * <p><strong>The confirmed count is the concurrency token.</strong> Apply sends the count the
 * operator was shown; the server re-counts inside its own transaction and answers `409` when the
 * eligible set has moved. The dialog shows that refusal, re-reads the count, and asks for the choice
 * to be confirmed again - rather than silently retrying against a scope nobody saw.</p>
 *
 * <p><strong>What it never does:</strong> it reaches no purchase, classification, cost, stock or
 * supplier rule, it never clears a rule (only Taxable and GST-free can be chosen at all), and
 * Cancel at either step writes nothing.</p>
 */
@Component({
  selector: 'app-product-gst-rule-bulk',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './product-gst-rule-bulk.component.html'
})
export class ProductGstRuleBulkComponent implements OnChanges {
  private static instances = 0;

  /**
   * Whether the question is open. The page owns that, because the page owns the control that asks
   * it; everything from the read to the write is this component's.
   */
  @Input() open = false;

  /** How many products were configured, once a change has committed. */
  @Output() readonly applied = new EventEmitter<number>();

  /** The dialog is finished with - cancelled, or applied. The page withdraws it. */
  @Output() readonly closed = new EventEmitter<void>();

  /** The two targets a bulk change may set. "No rule" is not one: see `BulkProductGstRulePolicy`. */
  readonly options: { value: GstClassification; label: string }[] = [
    { value: GstClassification.Taxable, label: 'Taxable' },
    { value: GstClassification.GstFree, label: 'GST-free' }
  ];

  /** The chosen rule, or `null` while nothing has been chosen - which is how it starts. */
  rule: GstClassification | null = null;

  step: BulkGstRuleStep = 'choose';

  /**
   * How many products carry no rule, as the server answered, or `null` while that is unread. `0` is
   * an answer of its own - every product already has a rule - and is never read as "unknown".
   */
  eligibleCount: number | null = null;

  loading = false;
  saving = false;
  error = '';

  /** This dialog's own heading id, so it is labelled by its own heading. */
  readonly titleId = `bulk-gst-rule-title-${++ProductGstRuleBulkComponent.instances}`;

  @ViewChild('firstOption') private readonly firstOption?: ElementRef<HTMLInputElement>;

  /** The open panel, while there is one, so withdrawing the question leaves the top layer. */
  private panel?: HTMLDialogElement;

  /** The listeners bound for one opening, undone when it closes with the element they belong to. */
  private readonly panelListeners: (() => void)[] = [];

  constructor(
    private readonly productService: ProductService,
    private readonly renderer: Renderer2
  ) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['open']) {
      return;
    }

    // Opening asks the question from the start every time: a previous answer, step, count or
    // failure describes a catalogue that has since been written to, or not written to, and neither
    // is the state this question starts from.
    this.reset();
    if (this.open) {
      this.readEligibleCount();
    }
  }

  @ViewChild('dialogPanel')
  private set dialogPanel(panel: ElementRef<HTMLDialogElement> | undefined) {
    if (panel === undefined) {
      this.closePanel();
      return;
    }

    this.openAsModal(panel.nativeElement);
  }

  /** Whether there is anything to configure at all, once the count has been read. */
  get hasEligibleProducts(): boolean {
    return this.eligibleCount !== null && this.eligibleCount > 0;
  }

  /** Whether the count has been read and is zero, which is the empty state and not a failure. */
  get hasNothingEligible(): boolean {
    return this.eligibleCount === 0;
  }

  get canContinue(): boolean {
    return this.rule !== null && this.hasEligibleProducts && !this.loading;
  }

  get canApply(): boolean {
    return this.canContinue && !this.saving;
  }

  /** The chosen rule's own label, so the confirmation names what was chosen rather than a number. */
  get chosenLabel(): string {
    return this.options.find((option) => option.value === this.rule)?.label ?? '';
  }

  chooseRule(rule: GstClassification): void {
    this.rule = rule;
  }

  continueToConfirmation(): void {
    if (!this.canContinue) {
      return;
    }

    this.error = '';
    this.step = 'confirm';
  }

  backToChoice(): void {
    if (this.saving) {
      return;
    }

    this.step = 'choose';
  }

  /** Re-reads the count after a failed read, which is the only way out of that state. */
  retry(): void {
    this.readEligibleCount();
  }

  cancel(): void {
    if (this.saving) {
      return;
    }

    this.closed.emit();
  }

  apply(): void {
    const rule = this.rule;
    const confirmedCount = this.eligibleCount;
    // Checked here as well as disabled in the template, so a second submission is actually
    // impossible rather than merely discouraged: the whole point of the confirmed count is that one
    // operator's one decision is applied once.
    if (rule === null || confirmedCount === null || this.saving || !this.hasEligibleProducts) {
      return;
    }

    this.saving = true;
    this.error = '';
    this.productService.setBulkGstRule(rule, confirmedCount).subscribe({
      next: (result) => {
        this.saving = false;
        this.applied.emit(result.changedCount);
        this.closed.emit();
      },
      error: (failure: unknown) => {
        this.saving = false;
        this.error = refusalMessage(
          failure,
          'Failed to set the GST rule on those products. Nothing was changed; try again.'
        );

        // A conflict means the eligible set moved, so the count on screen is wrong and the
        // confirmation it authorised is void. Go back to the choice with a fresh count, which is
        // what the server's own message asks for.
        if (failure instanceof HttpErrorResponse && failure.status === 409) {
          this.step = 'choose';
          this.readEligibleCount({ keepError: true });
        }
      }
    });
  }

  /**
   * The server's count of the products carrying no rule. `keepError` is set by the conflict path,
   * which has a message of its own to show beside the refreshed count.
   */
  private readEligibleCount(options: { keepError?: boolean } = {}): void {
    this.loading = true;
    if (!options.keepError) {
      this.error = '';
    }

    this.productService.previewBulkGstRule().subscribe({
      next: (preview) => {
        this.loading = false;
        this.eligibleCount = preview.eligibleCount;
      },
      error: () => {
        this.loading = false;
        // Not a count of zero: an unread count must never render as "nothing to do", which would
        // report a failure as a finished state.
        this.eligibleCount = null;
        this.error =
          'Failed to read how many products have no GST rule. Nothing was changed; try again.';
      }
    });
  }

  private reset(): void {
    this.rule = null;
    this.step = 'choose';
    this.eligibleCount = null;
    this.loading = false;
    this.saving = false;
    this.error = '';
  }

  /**
   * Hands the panel to the browser as a modal: the top layer, the inert page behind it and the
   * `::backdrop` are the platform's. The browser's own close request is answered as Cancel, because
   * dismissing a question is not answering it.
   */
  private openAsModal(panel: HTMLDialogElement): void {
    this.panel = panel;
    if (typeof panel.showModal === 'function') {
      panel.showModal();
    }

    this.firstOption?.nativeElement.focus();

    this.panelListeners.push(
      this.renderer.listen(panel, 'cancel', (event: Event) => {
        event.preventDefault();
        this.cancel();
      }),
      // `::backdrop` is the panel's own pseudo-element, so a click beside the panel is reported with
      // the panel as its target; a click on anything inside it never is.
      this.renderer.listen(panel, 'click', (event: MouseEvent) => {
        if (event.target === panel) {
          this.cancel();
        }
      })
    );
  }

  private closePanel(): void {
    for (const unlisten of this.panelListeners) {
      unlisten();
    }
    this.panelListeners.length = 0;

    const panel = this.panel;
    this.panel = undefined;
    panel?.close();
  }
}

import { CommonModule } from '@angular/common';
import { Component, EventEmitter, OnDestroy, OnInit, Output } from '@angular/core';
import { Subscription } from 'rxjs';
import { LocalMachine } from '../../../models/models';
import { LocalMachineService } from '../../../services/local-machine.service';
import {
  ApplyProviderMachineLinksRequest,
  ProviderMachineEntry,
  ProviderMachineLinkAction,
  ProviderMachineLinkDecision,
  ProviderMachineReview,
  ProviderMachineReviewService
} from '../../../services/provider-machine-review.service';
import { ToastService } from '../../../services/toast.service';
import { SupportSessionService } from '../../../support-session/support-session.service';

/** What the operator has answered about one provider machine, before it becomes a request decision. */
interface MachineAnswer {
  action: ProviderMachineLinkAction;
  localMachineId: number | null;
}

/**
 * Provider machine linking on the Machines page (issue #742, parent #329 child G3): the panel that
 * reports the provider's fleet against the business's own machines and settles the difference.
 *
 * <p>A dedicated feature component rather than more template on the Machines page, as the page
 * composition boundary requires (docs/architecture.md § Page composition boundary, issue #191): the
 * review, its load state, the per-machine answers, the apply and its refusal message all live
 * here.</p>
 *
 * <p><strong>It reviews as soon as it is shown, and the page only shows it when the fleet read can
 * be served.</strong> That is what makes the automatic half of the issue automatic: an operator who
 * connects a provider and opens Machines finds the new provider machines already recorded, with no
 * action of their own - but only for a business with no machine of its own lacking a provider id.
 * With any such machine the server links nothing and reports the provider machines here instead,
 * because any one of them may be a machine the operator already recorded by hand.</p>
 *
 * <p><strong>Nothing is matched by name.</strong> The screen offers exactly the two honest options
 * the server accepts - link this provider machine to one of your machines, or create a new one - and
 * neither is chosen for the operator. A machine of theirs that is called exactly what the provider
 * calls its machine is offered like any other, and is linked only if they say so.</p>
 *
 * <p>The panel is hidden entirely while a support session is active, on the established rule (issues
 * #554, #658, #661): both calls it makes are `POST`s the read-only session rule refuses, and the
 * Nayax credentials they need are refused in that state anyway.</p>
 */
@Component({
  selector: 'app-provider-machine-review',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './provider-machine-review.component.html'
})
export class ProviderMachineReviewComponent implements OnInit, OnDestroy {
  /**
   * Asks the hosting page to reload the machine list: a link changed what that list says, and the
   * list is what the server stored rather than what this panel believes.
   */
  @Output() readonly linked = new EventEmitter<void>();

  /** What linking states, worded once: it records the provider's id and changes nothing else. */
  static readonly LinkNotice =
    "The provider's machine id is recorded on the machine you choose. Its name stays yours - " +
    'nothing synchronises a machine name - and its history is unchanged.';

  static readonly CreateNotice =
    "A new machine is added, carrying the provider's name and its machine id.";

  static readonly UnnamedLinkMessage =
    'Choose the machine to link, or change that row to Create new or Skip. Nothing was applied.';

  readonly actions = ProviderMachineLinkAction;
  readonly linkNotice = ProviderMachineReviewComponent.LinkNotice;
  readonly createNotice = ProviderMachineReviewComponent.CreateNotice;

  review: ProviderMachineReview | null = null;

  /** True while the fleet is being read, and while an apply is outstanding. */
  reviewing = false;
  applying = false;

  /** Set when the fleet could not be read at all: the panel says so in place of its content. */
  failed = false;

  /** Set when the business's own machines could not be listed, so linking to one is unavailable. */
  machinesFailed = false;

  /** The machines a link may name. Fetched here, because the review reports ids only. */
  machines: LocalMachine[] = [];

  /** Whether a support session is active: the whole panel is a write surface, so it is withheld. */
  sessionActive = false;

  private readonly answers = new Map<number, MachineAnswer>();
  private readonly subscriptions = new Subscription();

  constructor(
    private readonly reviewService: ProviderMachineReviewService,
    private readonly localMachines: LocalMachineService,
    private readonly toast: ToastService,
    supportSession: SupportSessionService
  ) {
    this.subscriptions.add(supportSession.active$.subscribe((active) => (this.sessionActive = active)));
  }

  ngOnInit(): void {
    if (this.sessionActive) return;
    this.check();
    this.loadMachines();
  }

  ngOnDestroy(): void {
    this.subscriptions.unsubscribe();
  }

  get loading(): boolean {
    return this.reviewing || this.applying;
  }

  /** The provider machines the operator has to decide about, in the order the API reported them. */
  get awaitingReview(): ProviderMachineEntry[] {
    return this.review?.awaitingReview ?? [];
  }

  /** Whether the panel has anything at all to say, so the page can leave it out when it has not. */
  get hasSomethingToReport(): boolean {
    return (
      this.loading ||
      this.failed ||
      this.awaitingReview.length > 0 ||
      (this.review?.autoLinked.length ?? 0) > 0 ||
      (this.review?.linkedCount ?? 0) > 0
    );
  }

  /** How many rows carry an answer, so the Apply button can say what it is about to do. */
  get decidedCount(): number {
    return this.requestDecisions().length;
  }

  answer(providerMachineId: number): MachineAnswer {
    const existing = this.answers.get(providerMachineId);
    if (existing) return existing;

    const created: MachineAnswer = { action: ProviderMachineLinkAction.Defer, localMachineId: null };
    this.answers.set(providerMachineId, created);
    return created;
  }

  setAction(providerMachineId: number, action: ProviderMachineLinkAction | string): void {
    const answer = this.answer(providerMachineId);
    answer.action = Number(action) as ProviderMachineLinkAction;
    if (answer.action !== ProviderMachineLinkAction.LinkExisting) {
      // Only a link names a machine; a created machine has no id yet and a skipped row is not
      // written at all.
      answer.localMachineId = null;
    }
  }

  /** Records the machine a link names. The `<select>` hands back a string. */
  setLocalMachine(providerMachineId: number, localMachineId: number | string | null): void {
    const parsed = typeof localMachineId === 'string' ? Number.parseInt(localMachineId, 10) : localMachineId;
    this.answer(providerMachineId).localMachineId =
      parsed != null && Number.isFinite(parsed) ? parsed : null;
  }

  /**
   * The machines a link may name: the ones this business maintains itself. A machine the provider
   * already names carries a provider id, so linking it to a second provider machine is refused by
   * the server - it is not offered here either. A deactivated machine is offered and marked, because
   * a retired machine is still the provider's machine and linking it beats duplicating it.
   */
  get candidates(): LocalMachine[] {
    return this.machines.filter((machine) => !machine.isProviderNamed);
  }

  /** Reads the provider's fleet again, which is also what the operator's Check again asks for. */
  check(): void {
    if (this.loading || this.sessionActive) return;

    this.reviewing = true;
    this.failed = false;
    this.answers.clear();
    this.subscriptions.add(
      this.reviewService.review().subscribe({
        next: (review) => {
          this.reviewing = false;
          this.review = review;
          // Seeded up front, so reading a row's answer from the template is a pure read: every row
          // starts on Skip, the only honest default for a decision nobody took.
          for (const entry of review.awaitingReview) this.answer(entry.providerMachineId);
          // The read itself may have linked new provider machines, which changes what the machine
          // list says.
          if (review.autoLinked.length > 0) this.linked.emit();
        },
        error: (err) => {
          this.reviewing = false;
          this.review = null;
          this.failed = true;
          this.toast.error(this.errorMessage(err, 'The provider machine list could not be read.'));
        }
      })
    );
  }

  /**
   * Applies exactly what the operator answered. A row nobody answered is sent as nothing at all,
   * which is what makes a half-decided review leave the other half alone.
   *
   * A link that names no machine is refused here rather than sent: the server refuses the whole
   * apply over one such decision, so sending it would throw away every other answer on the screen.
   */
  applyDecisions(): void {
    // Checked here as well as disabled in the template, so the apply is actually unavailable rather
    // than merely inert - the endpoint refuses it either way.
    if (this.loading || this.sessionActive) return;

    const decisions = this.requestDecisions();
    if (decisions.length === 0) return;

    if (decisions.some((decision) => decision.action === ProviderMachineLinkAction.LinkExisting && decision.localMachineId == null)) {
      this.toast.error(ProviderMachineReviewComponent.UnnamedLinkMessage);
      return;
    }

    const request: ApplyProviderMachineLinksRequest = { decisions };
    this.applying = true;
    this.subscriptions.add(
      this.reviewService.apply(request).subscribe({
        next: (applied) => {
          this.applying = false;
          this.toast.success(
            `Linked ${applied.linkedCount} and added ${applied.createdCount} machine(s).`
          );
          this.linked.emit();
          // The review described the fleet before these links; a fresh reading is the only honest
          // next view, and it is also what reports whatever is still waiting.
          this.check();
          this.loadMachines();
        },
        error: (err) => {
          this.applying = false;
          this.toast.error(this.errorMessage(err, 'The provider machines could not be linked.'));
        }
      })
    );
  }

  private loadMachines(): void {
    this.subscriptions.add(
      this.localMachines.getAll().subscribe({
        next: (machines) => {
          this.machines = machines;
          this.machinesFailed = false;
        },
        error: () => {
          this.machines = [];
          this.machinesFailed = true;
        }
      })
    );
  }

  private requestDecisions(): ProviderMachineLinkDecision[] {
    const decisions: ProviderMachineLinkDecision[] = [];
    for (const entry of this.awaitingReview) {
      const answer = this.answers.get(entry.providerMachineId);
      if (!answer || answer.action === ProviderMachineLinkAction.Defer) continue;

      const decision: ProviderMachineLinkDecision = {
        providerMachineId: entry.providerMachineId,
        action: answer.action
      };
      if (answer.action === ProviderMachineLinkAction.LinkExisting) {
        decision.localMachineId = answer.localMachineId;
      }
      decisions.push(decision);
    }
    return decisions;
  }

  /** The refusal the server worded, or the given fallback sentence. */
  private errorMessage(error: unknown, fallback: string): string {
    const detail = (error as { error?: { detail?: unknown; message?: unknown } } | null)?.error;
    for (const candidate of [detail?.detail, detail?.message]) {
      if (typeof candidate === 'string' && candidate.trim().length > 0) return candidate;
    }
    return fallback;
  }
}

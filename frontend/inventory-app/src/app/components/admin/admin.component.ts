import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';

/**
 * The Admin landing page. Every Admin workflow now lives on its own routed page (issues #388,
 * #389, #390 and #433), so this component holds no workflow state, no service dependency and no
 * second copy of any tool - it is the link hub the Dashboard's "Open Admin" action and each
 * dedicated page's "Back to Admin" link point at. The sidebar's Admin group (#391) is now the
 * primary way into those pages, so this is a second, still valid entry point; it therefore keeps
 * its own `/admin` address rather than redirecting.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <h1 class="page-title">Admin</h1>
          <p class="page-subtitle">Imports and maintenance tools. These actions can change application data.</p>
        </div>
      </header>

      <div class="grid gap-5 md:grid-cols-2">
        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Nayax Settings</h2>
            <p class="mt-1 text-sm value-muted">Processing-fee rate configuration and configured-rate history.</p>
            <a routerLink="/admin/nayax-settings" class="btn btn-primary mt-4 inline-flex">Open Nayax Settings</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Site Commission Agreements</h2>
            <p class="mt-1 text-sm value-muted">Site commission agreement form and current agreements.</p>
            <a routerLink="/admin/site-commission-agreements" class="btn btn-primary mt-4 inline-flex">Open Site Commission Agreements</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Imports</h2>
            <p class="mt-1 text-sm value-muted">Nayax sales file import and template, product catalogue import and pending reimbursement XML import.</p>
            <a routerLink="/admin/imports" class="btn btn-primary mt-4 inline-flex">Open Imports</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Nayax Historical Cost Recovery</h2>
            <p class="mt-1 text-sm value-muted">Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.</p>
            <a routerLink="/admin/historical-cost-recovery" class="btn btn-primary mt-4 inline-flex">Open Nayax Historical Cost Recovery</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Inventory AVCO Transition Baseline</h2>
            <p class="mt-1 text-sm value-muted">Start reliable perpetual AVCO from a controlled cutover without changing incomplete legacy movements or historical Nayax-costed sales.</p>
            <a routerLink="/admin/avco-transition" class="btn btn-primary mt-4 inline-flex">Open Inventory AVCO Transition Baseline</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Costing Repair</h2>
            <p class="mt-1 text-sm value-muted">A human-entered historical costing repair for a product whose cost history has a fatal missing-opening or unknown-cost issue.</p>
            <a routerLink="/admin/costing-repair" class="btn btn-primary mt-4 inline-flex">Open Costing Repair</a>
          </div>
        </section>

        <section class="card">
          <div class="card-body">
            <h2 class="card-title">Historical GST Classification</h2>
            <p class="mt-1 text-sm value-muted">Classify purchase components that are still not classified from the GST rules configured on your products and suppliers, after previewing exactly what would change.</p>
            <a routerLink="/admin/historical-gst-classification" class="btn btn-primary mt-4 inline-flex">Open Historical GST Classification</a>
          </div>
        </section>
      </div>
    </div>
  `
})
export class AdminComponent {}

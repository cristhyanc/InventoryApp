import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';

/**
 * The Admin landing page. Every Admin workflow now lives on its own routed page (issues #388,
 * #389 and #390), so this component holds no workflow state, no service dependency and no second
 * copy of any tool - it is the link hub the Dashboard's "Open Admin" action and each dedicated
 * page's "Back to Admin" link point at. The sidebar's Admin group (#391) is now the primary way
 * into those pages, so this is a second, still valid entry point; it therefore keeps its own
 * `/admin` address rather than redirecting.
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <div class="mb-6">
      <h1 class="text-2xl font-semibold text-slate-800">Admin</h1>
      <p class="mt-1 text-sm text-slate-500">Imports and maintenance tools. These actions can change application data.</p>
    </div>

    <div class="grid gap-5 md:grid-cols-2">
      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Nayax Settings</h2>
        <p class="mt-1 text-sm text-slate-500">Processing-fee rate configuration and configured-rate history.</p>
        <a routerLink="/admin/nayax-settings" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Nayax Settings</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Site Commission Agreements</h2>
        <p class="mt-1 text-sm text-slate-500">Site commission agreement form and current agreements.</p>
        <a routerLink="/admin/site-commission-agreements" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Site Commission Agreements</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Imports</h2>
        <p class="mt-1 text-sm text-slate-500">Nayax sales file import and template, product catalogue import and pending reimbursement XML import.</p>
        <a routerLink="/admin/imports" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Imports</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Nayax Historical Cost Recovery</h2>
        <p class="mt-1 text-sm text-slate-500">Recover pending completed-sale COGS from transaction-level Nayax Product Cost Price without replacing finalized inventory-ledger costs.</p>
        <a routerLink="/admin/historical-cost-recovery" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Nayax Historical Cost Recovery</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Inventory AVCO Transition Baseline</h2>
        <p class="mt-1 text-sm text-slate-500">Start reliable perpetual AVCO from a controlled cutover without changing incomplete legacy movements or historical Nayax-costed sales.</p>
        <a routerLink="/admin/avco-transition" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Inventory AVCO Transition Baseline</a>
      </section>

      <section class="rounded-xl bg-white p-6 shadow-sm">
        <h2 class="text-lg font-semibold text-slate-800">Costing Repair</h2>
        <p class="mt-1 text-sm text-slate-500">A human-entered historical costing repair for a product whose cost history has a fatal missing-opening or unknown-cost issue.</p>
        <a routerLink="/admin/costing-repair" class="mt-4 inline-block rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white">Open Costing Repair</a>
      </section>
    </div>
  `
})
export class AdminComponent {}

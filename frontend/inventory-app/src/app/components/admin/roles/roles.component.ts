import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CAPABILITY_DESCRIPTIONS, ROLE_DEFINITIONS } from './role-definitions';

/**
 * The Admin → Roles page (issue #530, one slice of #503 under #328): a read-only explanation of
 * what Owner, Manager and Operator can each access, for an Owner to read.
 *
 * It edits nothing - no role, no permission and no member - which is #530's and #505's explicit
 * exclusions. The route already requires `Roles.View` (`app.routes.ts`), which only an Owner
 * holds; see `role-definitions.ts` for where this page's content comes from and how it is kept
 * from drifting away from the published capability vocabulary.
 */
@Component({
  selector: 'app-roles',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="page">
      <header class="page-header">
        <div>
          <a routerLink="/admin" class="btn-link text-sm">&larr; Back to Admin</a>
          <h1 class="page-title mt-2">Roles</h1>
          <p class="page-subtitle">What Owner, Manager and Operator can each access. This page changes nothing.</p>
        </div>
      </header>

      <div class="grid gap-5">
        @for (role of roleDefinitions; track role.role) {
          <section class="card">
            <div class="card-header">
              <h2 class="card-title">{{ role.role }}</h2>
            </div>
            <div class="card-body">
              <p class="text-sm value-muted">{{ role.summary }}</p>
              <ul class="mt-3 list-disc pl-5">
                @for (capability of role.capabilities; track capability) {
                  <li class="text-sm text-md-gray-800">{{ descriptions[capability] }}</li>
                }
              </ul>
            </div>
          </section>
        }
      </div>
    </div>
  `
})
export class RolesComponent {
  readonly roleDefinitions = ROLE_DEFINITIONS;
  readonly descriptions = CAPABILITY_DESCRIPTIONS;
}

import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Subscription } from 'rxjs';
import { SupportSessionService } from '../../support-session/support-session.service';

@Component({
  selector: 'app-products-shell',
  standalone: true,
  imports: [CommonModule, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './products-shell.component.html'
})
export class ProductsShellComponent implements OnDestroy {
  /**
   * Whether a support session is active (issue #737, extending #554): "New Product" is hidden
   * while it is, because a session grants `InventoryOperate` - the capability this write would
   * otherwise be gated on - and the backend refuses the write itself only by its read-only method
   * rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(supportSession: SupportSessionService) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }
}

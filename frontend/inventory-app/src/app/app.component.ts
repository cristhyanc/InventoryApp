import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ToastContainerComponent } from "./components/shared/toast-container.component";

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, ToastContainerComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss'
})
export class AppComponent {
  title = 'Inventory Manager';

  closeReportsMenu(event: Event): void {
    const target = event.target;
    if (target instanceof HTMLElement) {
      target.closest('details')?.removeAttribute('open');
    }
  }
}

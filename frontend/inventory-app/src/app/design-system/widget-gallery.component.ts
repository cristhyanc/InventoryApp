import { Component } from '@angular/core';
import { ConfirmationDialogComponent } from '../components/shared/confirmation-dialog.component';
import { IconComponent } from '../components/shared/icon.component';
import { ICON_PATHS } from '../components/shared/icon-paths';
import { MultiSelectDropdownComponent, MultiSelectOption } from '../components/shared/multi-select-dropdown.component';
import { LoadingService } from '../services/loading.service';
import { ToastService, ToastType } from '../services/toast.service';

/**
 * Rendering fixture for the four shared widgets #411 restyles and for every bundled icon, so the
 * series' visual evidence (Playwright screenshots at 1440px and 390px) can be produced from the
 * real components inside the real application shell.
 *
 * It is reachable only from `designSystemRoutes`, which is empty in any optimized build, and it
 * is never linked from navigation. It holds no business logic: its content is neutral synthetic
 * sample text, it calls no API, and the only state it keeps is which fixture is currently on
 * screen.
 *
 * The toast stack and the loading indicator live in the application shell, so this page drives
 * their services rather than rendering a second copy of either widget.
 */
@Component({
  selector: 'app-widget-gallery',
  standalone: true,
  imports: [ConfirmationDialogComponent, IconComponent, MultiSelectDropdownComponent],
  templateUrl: './widget-gallery.component.html'
})
export class WidgetGalleryComponent {
  /**
   * Long enough that a toast is still on screen when the screenshot is taken; the real callers
   * keep the service's 4s default.
   */
  private static readonly screenshotToastDurationMs = 600_000;

  readonly iconNames = Object.keys(ICON_PATHS);

  readonly toastVariants: readonly { type: ToastType; title: string; message: string }[] = [
    { type: 'success', title: 'Success', message: 'Sample item saved.' },
    { type: 'warning', title: 'Warning', message: 'Sample period is still provisional.' },
    { type: 'error', title: 'Error', message: 'Sample item could not be saved.' },
    { type: 'info', title: 'Info', message: 'Sample import finished with nothing to do.' }
  ];

  readonly sampleOptions: MultiSelectOption[] = [
    { id: 1, label: 'Sample product A' },
    { id: 2, label: 'Sample product B' },
    { id: 3, label: 'Sample product C' },
    { id: 4, label: 'Sample product D' }
  ];

  selectedOptionIds: number[] = [1, 3];
  isConfirmationOpen = false;

  constructor(
    private readonly toastService: ToastService,
    private readonly loadingService: LoadingService
  ) {}

  showToast(type: ToastType): void {
    const variant = this.toastVariants.find((candidate) => candidate.type === type);
    if (!variant) {
      return;
    }
    this.toastService.show(type, variant.message, variant.title, WidgetGalleryComponent.screenshotToastDurationMs);
  }

  showLoading(): void {
    this.loadingService.startRequest();
  }

  hideLoading(): void {
    this.loadingService.endRequest();
  }

  onSelectionChange(selectedIds: number[]): void {
    this.selectedOptionIds = selectedIds;
  }

  openConfirmation(): void {
    this.isConfirmationOpen = true;
  }

  closeConfirmation(): void {
    this.isConfirmationOpen = false;
  }
}

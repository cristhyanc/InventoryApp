import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { CategoryService } from '../../services/category.service';
import { Category } from '../../models/models';
import { ConfirmationDialogComponent } from '../shared/confirmation-dialog.component';
import { ToastService } from '../../services/toast.service';
import { apiErrorMessage } from '../shared/api-error-message';
import { SupportSessionService } from '../../support-session/support-session.service';

@Component({
  selector: 'app-category-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ConfirmationDialogComponent],
  templateUrl: './category-list.component.html'
})
export class CategoryListComponent implements OnInit, OnDestroy {
  categories: Category[] = [];
  editing: Category | null = null;
  error = '';
  confirmingCategory: Category | null = null;
  form = { name: '', description: '' };

  /**
   * Whether a support session is active (issue #737, extending #554): create/rename and delete are
   * hidden while it is, because a session grants `InventoryOperate` - the capability this page
   * would otherwise gate those on - and the backend refuses the write itself only by its read-only
   * method rule, never by withholding the capability. See `SupportSessionService.active$`.
   */
  sessionActive = false;

  private readonly sessionActiveSubscription: Subscription;

  constructor(
    private readonly categoryService: CategoryService,
    private readonly toastService: ToastService,
    supportSession: SupportSessionService
  ) {
    this.sessionActiveSubscription = supportSession.active$.subscribe((active) => (this.sessionActive = active));
  }

  ngOnInit(): void {
    this.load();
  }

  ngOnDestroy(): void {
    this.sessionActiveSubscription.unsubscribe();
  }

  load(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
  }

  /** Whether the provider names the category being edited, so its name may not be renamed here. */
  get nameLocked(): boolean {
    return (this.editing?.providerManagedFields ?? []).includes('name');
  }

  /**
   * Whether the provider names this category, so it cannot be deleted here either (issue #1026),
   * from the same signal {@link nameLocked} uses. The API refuses the delete regardless; this just
   * stops the row offering a Delete that must fail.
   */
  providerManaged(category: Category): boolean {
    return (category.providerManagedFields ?? []).includes('name');
  }

  startCreate(): void {
    this.editing = null;
    this.error = '';
    this.form = { name: '', description: '' };
  }

  startEdit(category: Category): void {
    this.editing = category;
    this.error = '';
    this.form = { name: category.name, description: category.description ?? '' };
  }

  cancel(): void {
    this.startCreate();
  }

  save(): void {
    // Checked here as well as disabled in the template, so the write is actually unavailable
    // rather than merely inert - the endpoint refuses it either way.
    if (this.sessionActive) {
      return;
    }
    if (!this.form.name.trim()) {
      this.error = 'Category name is required.';
      return;
    }
    this.error = '';

    const payload = { name: this.form.name.trim(), description: this.form.description || null };
    const onSuccess = () => {
      this.startCreate();
      this.load();
    };
    const onError = (error: unknown) => {
      this.error = apiErrorMessage(error, this.editing ? 'Failed to update category.' : 'Failed to create category.');
    };

    if (this.editing) {
      this.categoryService.update(this.editing.id, payload).subscribe({ next: onSuccess, error: onError });
    } else {
      this.categoryService.create(payload).subscribe({ next: onSuccess, error: onError });
    }
  }

  confirmDelete(category: Category): void {
    // Checked here as well as hidden in the template, so the write is actually unavailable rather
    // than merely invisible - the endpoint refuses it either way.
    if (this.sessionActive || this.providerManaged(category)) {
      return;
    }
    this.confirmingCategory = category;
  }

  cancelDelete(): void {
    this.confirmingCategory = null;
  }

  deleteConfirmed(): void {
    if (!this.confirmingCategory) {
      return;
    }

    const category = this.confirmingCategory;
    this.confirmingCategory = null;
    this.categoryService.delete(category.id).subscribe({
      next: () => {
        if (this.editing?.id === category.id) {
          this.startCreate();
        }
        this.load();
      },
      // Delete is offered unconditionally (issue #737): the API is the only place that knows
      // whether any product still uses the category, so a refusal is explained here rather than
      // predicted in advance (`CategoryCataloguePolicy.DeletionRefusal`).
      error: (error) => this.toastService.error(apiErrorMessage(error, 'Failed to delete category.'))
    });
  }
}

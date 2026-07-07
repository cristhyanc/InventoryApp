import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { CategoryService } from '../../services/category.service';
import { Category } from '../../models/models';

@Component({
  selector: 'app-category-list',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './category-list.component.html'
})
export class CategoryListComponent implements OnInit {
  categories: Category[] = [];
  editing: Category | null = null;
  form = { name: '', description: '' };

  constructor(private categoryService: CategoryService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.categoryService.getAll().subscribe((c) => (this.categories = c));
  }

  startCreate(): void {
    this.editing = null;
    this.form = { name: '', description: '' };
  }

  startEdit(category: Category): void {
    this.editing = category;
    this.form = { name: category.name, description: category.description ?? '' };
  }

  save(): void {
    if (!this.form.name.trim()) return;

    const payload = { name: this.form.name.trim(), description: this.form.description || null };

    if (this.editing) {
      this.categoryService.update(this.editing.id, payload).subscribe(() => {
        this.startCreate();
        this.load();
      });
    } else {
      this.categoryService.create(payload).subscribe(() => {
        this.startCreate();
        this.load();
      });
    }
  }

  remove(category: Category): void {
    if (!confirm(`Delete category "${category.name}"?`)) return;
    this.categoryService.delete(category.id).subscribe(() => this.load());
  }

  cancel(): void {
    this.startCreate();
  }
}

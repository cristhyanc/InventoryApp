import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

export type ToastType = 'success' | 'error' | 'warning' | 'info';

export interface ToastMessage {
  id: string;
  type: ToastType;
  title: string;
  message: string;
}

/**
 * The shortest time a shared toast stays on screen (issue #1142). It is also the default: a caller
 * may ask for longer, and a shorter request is raised to this.
 */
export const MIN_TOAST_DURATION_MS = 5000;

@Injectable({
  providedIn: 'root'
})
export class ToastService {
  private messagesSubject = new BehaviorSubject<ToastMessage[]>([]);
  messages$ = this.messagesSubject.asObservable();
  private nextId = 1;
  /** Each visible toast's pending auto-dismiss timer, so an early dismissal can cancel it. */
  private readonly timers = new Map<string, number>();

  success(message: string, title = 'Success'): void {
    this.show('success', message, title);
  }

  error(message: string, title = 'Error'): void {
    this.show('error', message, title);
  }

  warning(message: string, title = 'Warning'): void {
    this.show('warning', message, title);
  }

  info(message: string, title = 'Info'): void {
    this.show('info', message, title);
  }

  show(type: ToastType, message: string, title: string, duration = MIN_TOAST_DURATION_MS): void {
    const id = `toast-${this.nextId++}`;
    const toast: ToastMessage = { id, type, title, message };
    const current = this.messagesSubject.value;
    this.messagesSubject.next([...current, toast]);

    const visibleFor = Math.max(duration, MIN_TOAST_DURATION_MS);
    this.timers.set(id, window.setTimeout(() => this.remove(id), visibleFor));
  }

  /** Removes one toast now and cancels its auto-dismiss; an id that is already gone is ignored. */
  remove(id: string): void {
    const timer = this.timers.get(id);
    if (timer !== undefined) {
      window.clearTimeout(timer);
      this.timers.delete(id);
    }

    const current = this.messagesSubject.value;
    if (!current.some((toast) => toast.id === id)) {
      return;
    }
    this.messagesSubject.next(current.filter((toast) => toast.id !== id));
  }
}

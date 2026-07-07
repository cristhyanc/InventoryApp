import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';

export type ToastType = 'success' | 'error' | 'warning' | 'info';

export interface ToastMessage {
  id: string;
  type: ToastType;
  title: string;
  message: string;
}

@Injectable({
  providedIn: 'root'
})
export class ToastService {
  private messagesSubject = new BehaviorSubject<ToastMessage[]>([]);
  messages$ = this.messagesSubject.asObservable();
  private nextId = 1;

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

  show(type: ToastType, message: string, title: string, duration = 4000): void {
    const id = `toast-${this.nextId++}`;
    const toast: ToastMessage = { id, type, title, message };
    const current = this.messagesSubject.value;
    this.messagesSubject.next([...current, toast]);

    window.setTimeout(() => this.remove(id), duration);
  }

  remove(id: string): void {
    const current = this.messagesSubject.value;
    this.messagesSubject.next(current.filter((toast) => toast.id !== id));
  }
}

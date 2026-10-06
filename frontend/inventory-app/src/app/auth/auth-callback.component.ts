import { Component } from '@angular/core';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  template: `
    <div class="flex min-h-screen items-center justify-center bg-md-gray-100 p-6">
      <div class="card w-full max-w-sm text-center">
        <div class="card-body">Signing you in...</div>
      </div>
    </div>
  `
})
export class AuthCallbackComponent {}

import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ConfigService } from '../services/config.service';
import { BusinessApplicationRequest } from './business-application-rules';

/** The `POST /api/onboarding/business-applications` response body (issue #541). */
export interface SubmittedBusinessApplication {
  readonly name: string;
  readonly status: string;
  readonly appliedAtUtc: string | null;
}

/**
 * Submits the signed-in person's own business application to
 * `POST /api/onboarding/business-applications` (issue #541), for the application form (issue #543).
 *
 * The body is the six settings and nothing else: which business the application becomes, its
 * status, the applicant and their role are all the server's to decide. Every refusal - a rule the
 * form missed (`400`), onboarding switched off (`404`), an account that already belongs to a
 * business or has a different application waiting (`409`) and the rate limit (`429`) - reaches
 * the caller as the `HttpErrorResponse` it is; this service interprets none of them.
 */
@Injectable({ providedIn: 'root' })
export class BusinessApplicationService {
  constructor(
    private readonly http: HttpClient,
    private readonly config: ConfigService
  ) {}

  submit(request: BusinessApplicationRequest): Observable<SubmittedBusinessApplication> {
    return this.http.post<SubmittedBusinessApplication>(
      `${this.config.apiBaseUrl.replace(/\/$/, '')}/onboarding/business-applications`,
      request
    );
  }
}

import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export interface NayaxProcessingFeeRate {
  id?: number;
  effectiveFrom: string;
  feeExGst: number;
  createdAt?: string;
}

@Injectable({ providedIn: 'root' })
export class NayaxSettingsService {
  constructor(private http: HttpClient, private config: ConfigService) {}

  getRates(): Observable<NayaxProcessingFeeRate[]> {
    return this.http.get<NayaxProcessingFeeRate[]>(this.url);
  }

  saveRate(rate: NayaxProcessingFeeRate): Observable<NayaxProcessingFeeRate> {
    return this.http.post<NayaxProcessingFeeRate>(this.url, rate);
  }

  private get url(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/settings/nayax-processing-fee-rates`;
  }
}

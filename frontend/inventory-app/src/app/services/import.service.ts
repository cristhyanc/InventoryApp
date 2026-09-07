import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ConfigService } from './config.service';

export interface NayaxSalesImportResult {
  imported: number;
  updated: number;
  skipped: number;
}

export interface ImportedFileImportResult {
  importedFiles: number;
  importedReimbursements: number;
  skippedFiles: number;
  failedFiles: number;
}

@Injectable({ providedIn: 'root' })
export class ImportService {
  private get baseUrl(): string {
    return `${this.config.apiBaseUrl.replace(/\/$/, '')}/imports`;
  }

  constructor(private http: HttpClient, private config: ConfigService) {}

  importProducts(): Observable<boolean> {
    return this.http.post<boolean>(`${this.baseUrl}/products`, {});
  }

  importNayaxSales(file: File): Observable<NayaxSalesImportResult> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<NayaxSalesImportResult>(`${this.baseUrl}/nayax-sales`, formData);
  }

  importPendingXmlFiles(): Observable<ImportedFileImportResult> {
    return this.http.post<ImportedFileImportResult>(`${this.baseUrl}/pending-xml`, {});
  }
}

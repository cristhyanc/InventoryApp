import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { Machine } from '../models/models';

@Injectable({
  providedIn: 'root'
})
export class MachineService {
  private readonly baseUrl = '/api/machines';

  constructor(private http: HttpClient) { }

  getAll(): Observable<Machine> {
    return this.http.get<Machine>(this.baseUrl);
  }


}

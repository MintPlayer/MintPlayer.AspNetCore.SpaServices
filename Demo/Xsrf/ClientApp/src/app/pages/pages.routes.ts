import { Route } from "@angular/router";

export const PAGES_ROUTES: Route[] = [
  { path: '', pathMatch: 'full', loadComponent: () => import('./home/home.component').then(m => m.HomeComponent) },
  { path: 'counter', loadComponent: () => import('./counter/counter.component').then(m => m.CounterComponent) },
  { path: 'fetch-data', loadComponent: () => import('./fetch-data/fetch-data.component').then(m => m.FetchDataComponent) },
]

import { Routes } from '@angular/router';
import { NewPlan } from './pages/new-plan';
import { PlanPage } from './pages/plan';
import { RunPage } from './pages/run';
import { BundlePage, Discover, History } from './pages/lists';

export const routes: Routes = [
  { path: '', component: NewPlan, title: 'ForkBridge · New plan' },
  { path: 'plans/:id', component: PlanPage, title: 'ForkBridge · Plan' },
  { path: 'runs', component: History, title: 'ForkBridge · History' },
  { path: 'runs/:id', component: RunPage, title: 'ForkBridge · Run' },
  { path: 'bundles/:id', component: BundlePage, title: 'ForkBridge · Bundle' },
  { path: 'discover', component: Discover, title: 'ForkBridge · Discovery' },
  { path: '**', redirectTo: '' },
];

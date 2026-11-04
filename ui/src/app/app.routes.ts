import { Routes } from '@angular/router';
import { NewPlan } from './pages/new-plan';
import { PlanPage } from './pages/plan';
import { RunPage } from './pages/run';
import { BundlePage, Discover, History } from './pages/lists';
import { PolicyPage } from './pages/policy';
import { HarmonyPage } from './harmony/harmony.page';
import { SyncBasePage } from './harmony/syncbase.page';

export const routes: Routes = [
  { path: '', component: NewPlan, title: 'ForkBridge · New plan' },
  { path: 'plans/:id', component: PlanPage, title: 'ForkBridge · Plan' },
  { path: 'runs', component: History, title: 'ForkBridge · History' },
  { path: 'runs/:id', component: RunPage, title: 'ForkBridge · Run' },
  { path: 'bundles/:id', component: BundlePage, title: 'ForkBridge · Bundle' },
  { path: 'discover', component: Discover, title: 'ForkBridge · Discovery' },
  { path: 'policy', component: PolicyPage, title: 'ForkBridge · Policy & Config' },
  { path: 'harmony', component: HarmonyPage, title: 'ForkBridge · Harmony' },
  { path: 'harmony/:profile/syncbase', component: SyncBasePage, title: 'ForkBridge · SyncBase' },
  { path: '**', redirectTo: '' },
];

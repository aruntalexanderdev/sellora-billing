import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { CashFlowReport } from './cash-flow-report';
import { AccountsReportsService } from '../accounts-reports.service';
import { provideScopeStubs } from '../../../../shared/scope/scope-testing';

describe('CashFlowReport funding section', () => {
  it('shows funding by source separately from customer receipts', () => {
    const report = {
      dateFrom: null, dateTo: null, receivedFromCustomers: 40, paidToSuppliers: 0, expenses: 0, customerRefunds: 0, supplierRefunds: 0, refunds: 0, reversalsIn: 0, reversalsOut: 0,
      transfersIn: 0, transfersOut: 0, moneyIn: 40, moneyOut: 0, net: 40, months: [], baseCurrency: { symbol: '₹' },
      fundingBySource: [{ source: 'Loan', amount: 1000 }, { source: 'OwnerCapital', amount: 500 }], fundingTotal: 1500,
    };
    TestBed.configureTestingModule({
      imports: [CashFlowReport],
      providers: [...provideScopeStubs(), { provide: AccountsReportsService, useValue: { cashFlow: () => of(report) } }],
    });
    const fixture = TestBed.createComponent(CashFlowReport);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const section = el.querySelector('[data-testid="funding-section"]')!;
    expect(section.textContent).toContain('Loan');
    expect(section.textContent).toContain('Owner capital');
    expect(el.querySelector('[data-testid="funding-total"]')?.textContent).toContain('1,500.00');
    expect(el.textContent).toContain('Capital, loans and other funding');
  });
});

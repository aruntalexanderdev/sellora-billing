import { Pos } from './pos';

describe('POS catalog taxWarning', () => {
  const catalog = () => [{ productId: 'a', taxWarning: 'No sales tax profile applies today.' }, { productId: 'b', taxWarning: null }, { productId: 'c' }];
  const warn = (id: string) => Pos.prototype.taxWarningOf.call({ catalog }, id);

  it('returns the server warning of the product, else null', () => {
    expect(warn('a')).toBe('No sales tax profile applies today.');
    expect(warn('b')).toBeNull();
    expect(warn('c')).toBeNull();
    expect(warn('zzz')).toBeNull();
  });
});

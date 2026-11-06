import {
  breakdown,
  computeFactors,
  convert,
  costPerBaseUnit,
  formatQuantity,
  equivalenceChain,
  hasAtMostDecimals,
  isCategoryAllowed,
  isFactorInRange,
  isValidInnerQuantity,
  isWholeForBase,
  outerKeys,
  round10,
  suggestInnerQuantity,
  type ChainNode,
} from './unit-conversion';
import type { MeasurementCategory } from '../../features/masters/units/units.models';

const PEN: ChainNode[] = [
  { key: 'pc', innerKey: null, innerQuantity: 1, name: 'Piece' },
  { key: 'box', innerKey: 'pc', innerQuantity: 10, name: 'Box' },
  { key: 'hctn', innerKey: 'box', innerQuantity: 5, name: 'Half Carton' },
  { key: 'fctn', innerKey: 'hctn', innerQuantity: 2, name: 'Full Carton' },
];

const factorOf = (rows: ChainNode[], base: string) => {
  const result = computeFactors(rows, base);
  return Object.fromEntries([...result].map(([k, v]) => [k, v.factor]));
};

describe('unit-conversion', () => {
  describe('computeFactors', () => {
    it('Pen: Piece 1, Box 10, Half Carton 50, Full Carton 100 with depths', () => {
      const result = computeFactors(PEN, 'pc');

      expect(factorOf(PEN, 'pc')).toEqual({ pc: 1, box: 10, hctn: 50, fctn: 100 });
      expect([...result.values()].map((r) => r.depth)).toEqual([0, 1, 2, 3]);
      expect([...result.values()].every((r) => r.error === null)).toBe(true);
    });

    it('full carton of 144 (12 x 12)', () => {
      const rows: ChainNode[] = [
        { key: 'pc', innerKey: null, innerQuantity: 1, name: 'Piece' },
        { key: 'dz', innerKey: 'pc', innerQuantity: 12, name: 'Dozen' },
        { key: 'ctn', innerKey: 'dz', innerQuantity: 12, name: 'Carton' },
      ];
      expect(factorOf(rows, 'pc')['ctn']).toBe(144);
    });

    it('Rice: kg base, Gram 0.001, Bag 25 (no float noise)', () => {
      const rows: ChainNode[] = [
        { key: 'kg', innerKey: null, innerQuantity: 1, name: 'Kilogram' },
        { key: 'g', innerKey: 'kg', innerQuantity: 0.001, name: 'Gram' },
        { key: 'bag', innerKey: 'kg', innerQuantity: 25, name: 'Bag' },
        { key: 'box', innerKey: 'g', innerQuantity: 200, name: '200 g box' },
      ];
      expect(factorOf(rows, 'kg')).toEqual({ kg: 1, g: 0.001, bag: 25, box: 0.2 });
    });

    it('Oil: L base, mL 0.001, Bottle(500 mL) 0.5, Case(12 Bottle) 6', () => {
      const rows: ChainNode[] = [
        { key: 'l', innerKey: null, innerQuantity: 1, name: 'Litre' },
        { key: 'ml', innerKey: 'l', innerQuantity: 0.001, name: 'Millilitre' },
        { key: 'btl', innerKey: 'ml', innerQuantity: 500, name: 'Bottle' },
        { key: 'case', innerKey: 'btl', innerQuantity: 12, name: 'Case' },
      ];
      expect(factorOf(rows, 'l')).toEqual({ l: 1, ml: 0.001, btl: 0.5, case: 6 });
    });

    it('base-only product', () => {
      expect(factorOf([{ key: 'pc', innerKey: null, innerQuantity: 1, name: 'Piece' }], 'pc')).toEqual({ pc: 1 });
    });

    it('flags a self-cycle', () => {
      const rows: ChainNode[] = [PEN[0], { key: 'box', innerKey: 'box', innerQuantity: 10, name: 'Box' }];
      expect(computeFactors(rows, 'pc').get('box')?.error).toBe('cycle');
    });

    it('flags 2- and 3-cycles; rows leading into a cycle are orphans', () => {
      const rows: ChainNode[] = [
        PEN[0],
        { key: 'a', innerKey: 'b', innerQuantity: 2, name: 'A' },
        { key: 'b', innerKey: 'c', innerQuantity: 2, name: 'B' },
        { key: 'c', innerKey: 'a', innerQuantity: 2, name: 'C' },
        { key: 'd', innerKey: 'a', innerQuantity: 2, name: 'D' },
        { key: 'x', innerKey: 'y', innerQuantity: 2, name: 'X' },
        { key: 'y', innerKey: 'x', innerQuantity: 2, name: 'Y' },
      ];
      const result = computeFactors(rows, 'pc');
      expect(['a', 'b', 'c', 'x', 'y'].map((k) => result.get(k)?.error)).toEqual(['cycle', 'cycle', 'cycle', 'cycle', 'cycle']);
      expect(result.get('d')?.error).toBe('orphan');
      expect(result.get('pc')?.factor).toBe(1);
    });

    it('flags orphans (missing inner) and missing quantities', () => {
      const rows: ChainNode[] = [
        PEN[0],
        { key: 'box', innerKey: 'nope', innerQuantity: 10, name: 'Box' },
        { key: 'root2', innerKey: null, innerQuantity: 1, name: 'Second root' },
        { key: 'noqty', innerKey: 'pc', innerQuantity: null, name: 'No qty' },
        { key: 'over', innerKey: 'noqty', innerQuantity: 2, name: 'Over' },
      ];
      const result = computeFactors(rows, 'pc');
      expect(result.get('box')?.error).toBe('orphan');
      expect(result.get('root2')?.error).toBe('orphan');
      expect(result.get('noqty')?.error).toBe('invalidQuantity');
      expect(result.get('over')?.error).toBe('invalidQuantity');
    });
  });

  describe('outerKeys', () => {
    it('returns every row whose chain contains the key', () => {
      expect([...outerKeys(PEN, 'box')].sort()).toEqual(['fctn', 'hctn']);
      expect([...outerKeys(PEN, 'fctn')]).toEqual([]);
    });
  });

  describe('isCategoryAllowed (5 x 5)', () => {
    const cats: MeasurementCategory[] = ['Count', 'Weight', 'Volume', 'Length', 'Packaging'];
    // rows = base, columns = unit
    const expected: Record<MeasurementCategory, boolean[]> = {
      Count: [true, false, false, false, true],
      Weight: [false, true, false, false, true],
      Volume: [false, false, true, false, true],
      Length: [false, false, false, true, true],
      Packaging: [false, false, false, false, false],
    };
    for (const base of cats) {
      it(`base ${base}`, () => {
        expect(cats.map((unit) => isCategoryAllowed(base, unit))).toEqual(expected[base]);
      });
    }
  });

  describe('suggestInnerQuantity', () => {
    it('derives the ratio from standard factors', () => {
      expect(suggestInnerQuantity({ standardFactor: 0.001 }, { standardFactor: 1 })).toBe(0.001);
      expect(suggestInnerQuantity({ standardFactor: 1 }, { standardFactor: 0.001 })).toBe(1000);
      expect(suggestInnerQuantity({ standardFactor: 0.5 }, { standardFactor: 0.001 })).toBe(500);
    });

    it('is null when either unit has no standard factor', () => {
      expect(suggestInnerQuantity({ standardFactor: null }, { standardFactor: 1 })).toBeNull();
      expect(suggestInnerQuantity({ standardFactor: 1 }, { standardFactor: null })).toBeNull();
    });
  });

  describe('R5 isWholeForBase', () => {
    it('piece base (0 dp) rejects 2.5, accepts 10', () => {
      expect(isWholeForBase(2.5, 0)).toBe(false);
      expect(isWholeForBase(10, 0)).toBe(true);
    });

    it('kg base (3 dp) accepts 0.001 and 0.2, rejects 0.0005', () => {
      expect(isWholeForBase(0.001, 3)).toBe(true);
      expect(isWholeForBase(0.2, 3)).toBe(true);
      expect(isWholeForBase(0.0005, 3)).toBe(false);
    });
  });

  describe('bounds', () => {
    it('factor range 1e-6..1e9', () => {
      expect(isFactorInRange(1e-6)).toBe(true);
      expect(isFactorInRange(1e9)).toBe(true);
      expect(isFactorInRange(1e9 + 1)).toBe(false);
      expect(isFactorInRange(1e-7)).toBe(false);
    });

    it('inner quantity > 0, <= 1e9, max 6 dp', () => {
      expect(isValidInnerQuantity(0.000001)).toBe(true);
      expect(isValidInnerQuantity(0.0000001)).toBe(false);
      expect(isValidInnerQuantity(0)).toBe(false);
      expect(isValidInnerQuantity(-1)).toBe(false);
      expect(isValidInnerQuantity(null)).toBe(false);
      expect(isValidInnerQuantity(2e9)).toBe(false);
    });

    it('hasAtMostDecimals ignores float noise', () => {
      expect(hasAtMostDecimals(0.1 + 0.2, 1)).toBe(true);
      expect(hasAtMostDecimals(1.2345, 3)).toBe(false);
    });
  });

  describe('convert and round10', () => {
    it('converts between units via factors', () => {
      expect(convert(3, 100, 10)).toBe(30); // 3 Full Carton = 30 Box
      expect(convert(250, 0.001, 1)).toBe(0.25); // 250 g = 0.25 kg
      expect(convert(3, 0.1, 1)).toBe(0.3);
    });

    it('round10 strips binary noise', () => {
      expect(round10(0.1 * 3)).toBe(0.3);
    });
  });

  describe('equivalenceChain', () => {
    it('Pen chain from Full Carton', () => {
      expect(equivalenceChain(PEN, 'fctn')).toBe('1 Full Carton = 2 Half Carton = 10 Box = 100 Piece');
      expect(equivalenceChain(PEN, 'box')).toBe('1 Box = 10 Piece');
      expect(equivalenceChain(PEN, 'pc')).toBe('1 Piece');
    });

    it('fractional chain without float noise', () => {
      const rows: ChainNode[] = [
        { key: 'kg', innerKey: null, innerQuantity: 1, name: 'kg' },
        { key: 'g', innerKey: 'kg', innerQuantity: 0.001, name: 'Gram' },
        { key: 'box', innerKey: 'g', innerQuantity: 200, name: 'Box' },
      ];
      expect(equivalenceChain(rows, 'box')).toBe('1 Box = 200 Gram = 0.2 kg');
    });

    it('is null on a cycle or a missing inner', () => {
      const rows: ChainNode[] = [
        { key: 'a', innerKey: 'b', innerQuantity: 2, name: 'A' },
        { key: 'b', innerKey: 'a', innerQuantity: 2, name: 'B' },
        { key: 'c', innerKey: 'zzz', innerQuantity: 2, name: 'C' },
      ];
      expect(equivalenceChain(rows, 'a')).toBeNull();
      expect(equivalenceChain(rows, 'c')).toBeNull();
    });
  });

  describe('breakdown', () => {
    it('122 pc -> 1 Full Carton, 2 Box, 2 Piece (greedy, zero parts omitted)', () => {
      const result = breakdown(122, [
        { key: 'pc', factor: 1 },
        { key: 'box', factor: 10 },
        { key: 'hctn', factor: 50 },
        { key: 'fctn', factor: 100 },
      ]);
      expect(result.parts).toEqual([
        { key: 'fctn', quantity: 1 },
        { key: 'box', quantity: 2 },
        { key: 'pc', quantity: 2 },
      ]);
      expect(result.remainder).toBe(0);
    });

    it('keeps a remainder when no unit absorbs it', () => {
      const result = breakdown(26.5, [{ key: 'bag', factor: 25 }]);
      expect(result.parts).toEqual([{ key: 'bag', quantity: 1 }]);
      expect(result.remainder).toBe(1.5);
    });

    it('handles fractional factors exactly', () => {
      const result = breakdown(0.7, [
        { key: 'kg', factor: 1 },
        { key: 'box', factor: 0.2 },
        { key: 'g', factor: 0.001 },
      ]);
      expect(result.parts).toEqual([
        { key: 'box', quantity: 3 },
        { key: 'g', quantity: 100 },
      ]);
    });
  });
});

describe('formatQuantity', () => {
  it('uses the unit decimal places', () => {
    expect(formatQuantity(300, 0)).toBe('300');
    expect(formatQuantity(1.5, 3)).toBe('1.500');
    expect(formatQuantity(2, 2)).toBe('2.00');
  });

  it('strips float noise', () => {
    expect(formatQuantity(0.1 * 3, 3)).toBe('0.300');
    expect(formatQuantity(0.1 + 0.2, 0)).toBe('0.3');
  });

  it('clamps decimal places to 0–3', () => {
    expect(formatQuantity(1.23456, 6)).toBe('1.235');
    expect(formatQuantity(4, -2)).toBe('4');
    expect(formatQuantity(4, null)).toBe('4');
  });

  it('never silently rounds stored precision below 3 dp', () => {
    expect(formatQuantity(2.5, 0)).toBe('2.5');
    expect(formatQuantity(1.25, 1)).toBe('1.25');
  });

  it('handles negatives, zero and non-finite values', () => {
    expect(formatQuantity(-15, 0)).toBe('-15');
    expect(formatQuantity(-0, 2)).toBe('0.00');
    expect(formatQuantity(Number.NaN, 0)).toBe('—');
  });
});

describe('costPerBaseUnit', () => {
  it('divides an entered-unit cost by the factor', () => {
    expect(costPerBaseUnit(250, 100)).toBe(2.5);
    expect(costPerBaseUnit(100, 12)).toBeCloseTo(8.333333, 6);
    expect(costPerBaseUnit(40, 1)).toBe(40);
  });
});

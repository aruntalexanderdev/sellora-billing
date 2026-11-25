import { CSV_BOM, csvBlob, csvEscape, toCsv } from './csv';

describe('csv', () => {
  it('quotes commas, quotes and line breaks and doubles inner quotes', () => {
    expect(csvEscape('a,b')).toBe('"a,b"');
    expect(csvEscape('say "hi"')).toBe('"say ""hi"""');
    expect(csvEscape('x\ny')).toBe('"x\ny"');
    expect(csvEscape('plain')).toBe('plain');
  });
  it('writes null/undefined as empty and numbers unformatted', () => {
    expect(csvEscape(null)).toBe('');
    expect(csvEscape(undefined)).toBe('');
    expect(csvEscape(1234.5)).toBe('1234.5');
    expect(csvEscape(-3)).toBe('-3');
  });
  it('neutralises formula-looking text but keeps numeric text', () => {
    expect(csvEscape('=SUM(A1)')).toBe("'=SUM(A1)");
    expect(csvEscape('-5')).toBe('-5');
  });
  it('joins rows with CRLF', () => {
    expect(toCsv(['a', 'b'], [[1, 'x,y']])).toBe('a,b\r\n1,"x,y"');
  });
  it('prefixes the blob with a UTF-8 BOM', async () => {
    const bytes = new Uint8Array(await csvBlob(['h'], [['₹']]).arrayBuffer());
    expect(Array.from(bytes.slice(0, 3))).toEqual([0xef, 0xbb, 0xbf]);
    expect(CSV_BOM).toBe('\uFEFF');
    expect(new TextDecoder('utf-8', { ignoreBOM: true }).decode(bytes)).toContain('₹');
  });
});

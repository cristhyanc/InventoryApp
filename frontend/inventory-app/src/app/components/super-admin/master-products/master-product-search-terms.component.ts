import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MAX_FLAVOUR_ALIASES_PER_ENTRY,
  MAX_SEARCH_PHRASES_PER_ENTRY,
  SupplierSearchTerms
} from '../../../services/super-admin-master-products.service';
import {
  SUPPLIER_PRICE_CONNECTOR_LABELS,
  SuperAdminSupplierPreset,
  SupplierPriceConnector
} from '../../../services/super-admin-supplier-presets.service';

/** One place a search entry can apply: one supplier, or every supplier on one connector. */
interface TargetOption {
  readonly key: string;
  readonly label: string;
}

/** One entry as the editor holds it while a person types: a target key and the raw lists. */
interface EditableEntry {
  target: string;
  phrases: string[];
  aliases: string[];
}

const connectorKey = (connector: SupplierPriceConnector): string => `connector:${connector}`;
const presetKey = (presetId: number): string => `preset:${presetId}`;

/**
 * The supplier search terms of one master product (issue #1138): for one supplier, or for every
 * supplier on one connector, up to two search phrases tried in order and the flavour aliases that
 * supplier uses.
 *
 * It is a child of the master product form rather than markup in it because it owns its own list
 * state - adding, reordering and removing entries, phrases and aliases - and reports the whole list
 * upwards through {@link entriesChange}; the form sends it with the correction it already makes.
 *
 * **It decides nothing the server does not.** Trimming, deduplicating, the two-phrase limit, the
 * one-entry-per-target rule and whether a supplier exists are all checked again on the server, whose
 * own sentence the form shows. What this editor withholds is only what it cannot represent: a third
 * phrase field and a target another entry already uses.
 */
@Component({
  selector: 'app-master-product-search-terms',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './master-product-search-terms.component.html'
})
export class MasterProductSearchTermsComponent implements OnChanges {
  /** The stored entries to start from. A new array (another product) starts the editor again. */
  @Input() entries: readonly SupplierSearchTerms[] = [];

  /** The supplier presets an entry may name. Connectors are always offered. */
  @Input() presets: readonly SuperAdminSupplierPreset[] = [];

  /** Raised with the whole list after every change, in the order shown. */
  @Output() readonly entriesChange = new EventEmitter<SupplierSearchTerms[]>();

  rows: EditableEntry[] = [];

  readonly maxPhrases = MAX_SEARCH_PHRASES_PER_ENTRY;
  readonly maxAliases = MAX_FLAVOUR_ALIASES_PER_ENTRY;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['entries']) {
      this.rows = (this.entries ?? []).map((entry) => ({
        target: targetKeyOf(entry),
        phrases: [...entry.searchPhrases],
        aliases: [...entry.flavourAliases]
      }));
    }
  }

  /**
   * Every connector, then every supplier collected on one, by name. A stored entry naming a
   * supplier this list does not hold is still shown, by its number, so it can be read and removed.
   */
  get targetOptions(): TargetOption[] {
    const connectors = (Object.keys(SUPPLIER_PRICE_CONNECTOR_LABELS) as unknown as string[])
      .map((value) => Number(value) as SupplierPriceConnector)
      .map((connector) => ({
        key: connectorKey(connector),
        label: `All ${SUPPLIER_PRICE_CONNECTOR_LABELS[connector]} suppliers`
      }));

    const presets = this.presets
      .filter((preset) => preset.priceConnector !== null)
      .map((preset) => ({ key: presetKey(preset.id), label: preset.name }))
      .sort((left, right) => left.label.localeCompare(right.label));

    const known = new Set([...connectors, ...presets].map((option) => option.key));
    const unknown = this.rows
      .map((row) => row.target)
      .filter((target) => target.startsWith('preset:') && !known.has(target))
      .map((target) => ({ key: target, label: `Supplier #${target.slice('preset:'.length)}` }));

    return [...connectors, ...presets, ...unknown];
  }

  /** Whether another entry already uses this target, so it is not offered twice. */
  isTaken(key: string, rowIndex: number): boolean {
    return this.rows.some((row, index) => index !== rowIndex && row.target === key);
  }

  /** Whether every entry names a supplier or a connector, which the form checks before saving. */
  get complete(): boolean {
    return this.rows.every((row) => row.target !== '');
  }

  addEntry(): void {
    this.rows.push({ target: '', phrases: [''], aliases: [] });
    this.emit();
  }

  removeEntry(index: number): void {
    this.rows.splice(index, 1);
    this.emit();
  }

  setTarget(index: number, target: string): void {
    this.rows[index].target = target;
    this.emit();
  }

  addPhrase(index: number): void {
    if (this.rows[index].phrases.length < this.maxPhrases) {
      this.rows[index].phrases.push('');
      this.emit();
    }
  }

  setPhrase(index: number, phraseIndex: number, value: string): void {
    this.rows[index].phrases[phraseIndex] = value;
    this.emit();
  }

  /** Swaps a phrase with the one before it: the order is the order the collector tries them in. */
  movePhraseUp(index: number, phraseIndex: number): void {
    const phrases = this.rows[index].phrases;
    if (phraseIndex > 0) {
      [phrases[phraseIndex - 1], phrases[phraseIndex]] = [phrases[phraseIndex], phrases[phraseIndex - 1]];
      this.emit();
    }
  }

  removePhrase(index: number, phraseIndex: number): void {
    this.rows[index].phrases.splice(phraseIndex, 1);
    this.emit();
  }

  addAlias(index: number): void {
    if (this.rows[index].aliases.length < this.maxAliases) {
      this.rows[index].aliases.push('');
      this.emit();
    }
  }

  setAlias(index: number, aliasIndex: number, value: string): void {
    this.rows[index].aliases[aliasIndex] = value;
    this.emit();
  }

  removeAlias(index: number, aliasIndex: number): void {
    this.rows[index].aliases.splice(aliasIndex, 1);
    this.emit();
  }

  private emit(): void {
    this.entriesChange.emit(
      this.rows.map((row) => ({
        supplierPresetId: row.target.startsWith('preset:') ? Number(row.target.slice('preset:'.length)) : null,
        priceConnector: row.target.startsWith('connector:')
          ? (Number(row.target.slice('connector:'.length)) as SupplierPriceConnector)
          : null,
        searchPhrases: [...row.phrases],
        flavourAliases: [...row.aliases]
      }))
    );
  }
}

/** The editor's key for an entry's target, or '' when it names none. */
function targetKeyOf(entry: SupplierSearchTerms): string {
  if (entry.supplierPresetId !== null) {
    return presetKey(entry.supplierPresetId);
  }

  return entry.priceConnector !== null ? connectorKey(entry.priceConnector) : '';
}

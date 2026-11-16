import type { ApiListOfCapabilitySnapshot, CombatSummary, EventEnvelopeOfCombatSummary, GameObservationSnapshot,
  RoomScoreDraftSnapshot, MusicScoreSelection, RoomScoreMappingRequest, InventorySnapshot, InventoryItem,
  MusicOwnedInstrumentPage, MusicOwnedInstrumentQuery, MusicOwnedScorePage,
  MusicOwnedScoreQuery, CombatDamageGroup, CombatDamageEvent, CombatEntityObservation } from '../../../artifacts/generated/sdk/typescript/client';

function verifyCombatDetail(group: CombatDamageGroup, hit: CombatDamageEvent, entity: CombatEntityObservation): void {
  const count: string | null | undefined = group.powerfulHitCount;
  const minimum: string | null | undefined = group.minimumDamage;
  const flag: boolean | null | undefined = hit.doubleHit;
  const maximum: string | null | undefined = entity.breakGauge?.maximum;
  const current: number | null | undefined = entity.breakGauge?.current;
  // @ts-expect-error 次数及整数伤害保留十进制字符串。
  const numericCount: number = group.powerfulHitCount;
  // @ts-expect-error 当前破防值允许小数。
  const stringCurrent: string = entity.breakGauge?.current;
  const breaked: boolean | null | undefined = hit.breakedHit;
  const elements: ReadonlyArray<string> | null | undefined = hit.elements;
  const knownElement: NonNullable<CombatDamageEvent['elements']>[number] = 'lightning';
  const futureElement: NonNullable<CombatDamageEvent['elements']>[number] = 'future-element';
  const fire: string | undefined = group.elementCounts?.fire;
  const multi: string | null | undefined = group.multiHitCount;
  // @ts-expect-error 元素次数保留十进制字符串。
  const numericFire: number | undefined = group.elementCounts?.fire;
  void [count, minimum, flag, maximum, current, numericCount, stringCurrent, breaked, elements, knownElement, futureElement,
    fire, multi, numericFire];
}
void verifyCombatDetail;

function verifyTypes(list: ApiListOfCapabilitySnapshot, event: EventEnvelopeOfCombatSummary): void {
  const id: string = list.items[0].id;
  const summary: CombatSummary = event.data;
  const dps: number | null = summary.encounterDps;
  const identity: string = summary.localEntity.rawInstanceId;
  const futureQuality: CombatSummary['quality'] = 'future-quality';
  // @ts-expect-error 64 位标识必须保持字符串。
  const numericIdentity: number = summary.localEntity.rawInstanceId;
  // @ts-expect-error 空值要求调用方显式处理。
  const requiredDps: number = summary.encounterDps;
  void [id, dps, identity, numericIdentity, requiredDps, futureQuality];
}
void verifyTypes;

function verifyObservation(snapshot: GameObservationSnapshot): void {
  const id: string | undefined = snapshot.latest?.value.identity?.characterId;
  const onset: number | null | undefined = snapshot.latest?.value.music?.estimatedStartMonotonicMs;
  const verified: boolean | undefined = snapshot.latest?.value.music?.playbackTimingVerified;
  // @ts-expect-error 观测角色标识必须保持字符串。
  const numericId: number = snapshot.latest!.value.identity!.characterId;
  void [id, onset, verified, numericId];
}
void verifyObservation;

function verifyScoreDraft(snapshot: RoomScoreDraftSnapshot, selection: MusicScoreSelection): void {
  const revision: string | undefined = snapshot.draft?.revision;
  const content: string | null | undefined = snapshot.draft?.score.contentSha256;
  const raw: string | undefined = snapshot.local?.score?.entry.rawTitle;
  const mapping: RoomScoreMappingRequest = { score: selection, instrumentId: 'piano' };
  // @ts-expect-error 来源修订使用十进制字符串。
  const numericRevision: number = selection.sourceRevision;
  // @ts-expect-error 草稿内容摘要可以未知。
  const certainContent: string = snapshot.draft!.score.contentSha256;
  void [revision, content, raw, mapping, numericRevision, certainContent];
}
void verifyScoreDraft;

function verifyInventory(snapshot: InventorySnapshot): void {
  const quantity: string | null | undefined = snapshot.data?.items[0].item.quantity;
  const gold: string | null | undefined = snapshot.data?.gold;
  const item: InventoryItem = { category: 'EquipmentTool', container: 'backpack', key: 'instrument-a', typeId: '1001',
    instanceId: '18446744073709551615', quantity: '1', attributes: {}, sheet: 'EquipmentTool', equipped: true };
  // @ts-expect-error 道具数量使用规范十进制字符串。
  const numberQuantity: number = snapshot.data!.items[0].item.quantity;
  // @ts-expect-error 派生类型键由业务层生成，不进入库存条目契约。
  const derivedType = item.type;
  void [quantity, gold, item, numberQuantity, derivedType];
}
void verifyInventory;

function verifyOwnedInstruments(page: MusicOwnedInstrumentPage): void {
  const query: MusicOwnedInstrumentQuery = { container: 'backpack', pageSize: 100, cursor: null, currentOnly: true };
  const quantity: string | null = page.items[0].quantity;
  const clientIdentity: string | null = page.items[0].clientInstrumentId;
  const futureMapping: MusicOwnedInstrumentPage['items'][number]['mapping'] = 'future-mapping';
  // @ts-expect-error 客户端音色 ID 使用十进制字符串。
  const numericInstrument: number = page.items[0].instrumentTypeId;
  // @ts-expect-error 未知数量要求调用方显式处理。
  const knownQuantity: string = page.items[0].quantity;
  void [query, quantity, clientIdentity, futureMapping, numericInstrument, knownQuantity];
}
void verifyOwnedInstruments;

function verifyOwnedScores(page: MusicOwnedScorePage): void {
  const query: MusicOwnedScoreQuery = { container: 'backpack', pageSize: 100, cursor: null, currentOnly: true };
  const instance: string | null = page.items[0].instanceId;
  const sheet: string | null = page.items[0].sheetId;
  const title: string | null = page.items[0].rawTitle;
  const acquired: string | null = page.items[0].acquiredTimeRaw;
  const composerRealm: number | null = page.items[0].composerRealmId;
  const copyingAllowed: boolean | null = page.items[0].isCopyingAllowed;
  const complete: boolean = page.complete;
  const futureQuality: MusicOwnedScorePage['items'][number]['quality'] = 'future-quality';
  // @ts-expect-error 乐谱实例标识使用规范十进制字符串。
  const numericInstance: number = page.items[0].instanceId;
  // @ts-expect-error 未知标题要求调用方显式处理。
  const certainTitle: string = page.items[0].rawTitle;
  void [query, instance, sheet, title, acquired, composerRealm, copyingAllowed, complete, futureQuality, numericInstance, certainTitle];
}
void verifyOwnedScores;

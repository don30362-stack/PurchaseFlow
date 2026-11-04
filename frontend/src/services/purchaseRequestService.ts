import { apiRequest } from '@/services/api'

import type {
    CreatePurchaseRequestInput,
    CreatedPurchaseRequest,
    PurchaseRequestDetail,
    PurchaseRequestListView,
    PurchaseRequestSummary,
    SubmitPurchaseRequestInput,
    UpdatePurchaseRequestInput,
} from '@/types/purchaseRequest'

// 取得申請清單
export async function getPurchaseRequests(
    view: PurchaseRequestListView,
): Promise<PurchaseRequestSummary[]> {
    return apiRequest<PurchaseRequestSummary[]>(
        `/api/purchase-requests?view=${view}`,
    )
}

// 取得單筆申請明細
export async function getPurchaseRequest(
    id: number,
): Promise<PurchaseRequestDetail> {
    return apiRequest<PurchaseRequestDetail>(
        `/api/purchase-requests/${id}`,
    )
}

// 建立新草稿
export async function createPurchaseRequest(
    input: CreatePurchaseRequestInput,
): Promise<CreatedPurchaseRequest> {
    return apiRequest<CreatedPurchaseRequest>(
        '/api/purchase-requests',
        {
            method: 'POST',
            body: input,
        },
    )
}

// 修改既有草稿（完整覆蓋品項）
export async function updatePurchaseRequest(
    id: number,
    input: UpdatePurchaseRequestInput,
): Promise<PurchaseRequestDetail> {
    return apiRequest<PurchaseRequestDetail>(
        `/api/purchase-requests/${id}`,
        {
            method: 'PUT',
            body: input,
        },
    )
}

// 將自己的 Draft 送出審核
export async function submitPurchaseRequest(
  id: number,
  input: SubmitPurchaseRequestInput,
): Promise<PurchaseRequestDetail> {
  return apiRequest<PurchaseRequestDetail>(
    `/api/purchase-requests/${id}/submit`,
    {
      method: 'POST',
      body: input,
    },
  )
}


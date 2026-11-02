export type PurchaseRequestStatus =
    | 'Draft'
    | 'Pending'
    | 'Approved'
    | 'Rejected'

export type PurchaseRequestListView =
    | 'mine'
    | 'pending'
    | 'reviewed'

export interface PurchaseRequestSummary {
    id: number
    title: string
    purpose: string | null
    status: PurchaseRequestStatus
    totalAmount: number
    createdAtUtc: string
    updatedAtUtc: string
    submittedAtUtc: string | null
    rowVersion: string
}

export interface PurchaseRequestItemInput {
    name: string
    quantity: number
    estimatedUnitPrice: number
}

export interface PurchaseRequestItem extends PurchaseRequestItemInput {
    id: number
    lineTotal: number
}

export interface CreatePurchaseRequestInput {
    title: string
    purpose: string | null
    items: PurchaseRequestItemInput[]
}

export interface UpdatePurchaseRequestInput
    extends CreatePurchaseRequestInput {
    rowVersion: string
}

// POST 建立草稿的回應不包含時間欄位
export interface CreatedPurchaseRequest {
    id: number
    title: string
    purpose: string | null
    status: 'Draft'
    totalAmount: number
    rowVersion: string
    items: PurchaseRequestItem[]
    approvalLog: null
}

export interface PurchaseRequestApprovalLog {
    id: number
    reviewerId: string
    decision: 'Approved' | 'Rejected'
    comment: string | null
    reviewedAtUtc: string
}

// GET 明細與 PUT 更新的完整回應
export interface PurchaseRequestDetail
    extends PurchaseRequestSummary {
    items: PurchaseRequestItem[]
    approvalLog: PurchaseRequestApprovalLog | null
}

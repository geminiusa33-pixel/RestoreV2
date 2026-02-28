export interface Order {
    id: number
    buyerEmail: string
    shippingAddress: ShippingAddress
    orderDate: string
    orderItems: OrderItem[]
    subtotal: number
    deliveryFee: number
  productDiscount: number
    discount: number
    total: number
    orderStatus: string
    refundRequestStatus: string
    refundRequestedAt?: string | null
    refundReviewedAt?: string | null
    refundReturnMethod?: string | null
    refundRequestReason?: string | null
    refundReviewNote?: string | null
    trackingNumber?: string | null
    trackingAddedAt?: string | null
    paymentSummary: PaymentSummary
    customerComment?: string | null
    customerCommentedAt?: string | null
    adminCommentReply?: string | null
    adminCommentRepliedAt?: string | null
  }
  
  export interface ShippingAddress {
    name: string
    line1: string
    line2?: string | null
    city: string
    state: string
    postal_code: string
    country: string
  }
  
  export interface OrderItem {
    productId: number
    productVariantId?: number | null
    variantColor?: string | null
    name: string
    pictureUrl: string
    price: number
    quantity: number
  }
  
  export interface PaymentSummary {
    last4: number | string
    brand: string
    exp_month: number
    exp_year: number
  }
  
  export interface CreateOrder {
    shippingAddress: ShippingAddress
    paymentSummary: PaymentSummary
    // Optional billing data (Portugal - NIF/fiscal invoice)
    billingTaxId?: string | null
  }
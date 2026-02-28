import { Box, Button, Checkbox, FormControlLabel, Paper, Step, StepLabel, Stepper, Typography, useTheme, useMediaQuery, TextField } from "@mui/material";
import { AddressElement, PaymentElement, useElements, useStripe } from "@stripe/react-stripe-js";
import { useState } from "react"
import Review from "./Review";
import { useFetchAddressQuery, useUpdateUserAddressMutation } from "../account/accountApi";
import { ConfirmationToken, StripeAddressElementChangeEvent, StripePaymentElementChangeEvent } from "@stripe/stripe-js";
import { useBasket } from "../../lib/hooks/useBasket";
import { currencyFormat } from "../../lib/util";
import { toast } from "react-toastify";
import { useNavigate } from "react-router-dom";
import { LoadingButton } from "@mui/lab";
import { useCreateOrderMutation } from "../orders/orderApi";
import { primaryActionSx, secondaryActionSx } from "../../app/shared/styles/actionButtons";

const steps = ['Morada', 'Pagamento', 'Revisão'];

export default function CheckoutStepper() {
    const theme = useTheme();
    const isSmall = useMediaQuery(theme.breakpoints.down('sm'));
    const [activeStep, setActiveStep] = useState(0);
    const [createOrder] = useCreateOrderMutation();
    const {basket, total, clearBasket, productDiscount, couponDiscount} = useBasket();
    const {data, isLoading} = useFetchAddressQuery();
    const [updateAddress] = useUpdateUserAddressMutation();
    const [saveAddressChecked, setSaveAddressChecked] = useState(false);
    const elements = useElements();
    const stripe = useStripe();
    const [addressComplete, setAddressComplete] = useState(false);
    const [paymentComplete, setPaymentComplete] = useState(false);
    const [phone, setPhone] = useState('');
    const [billingTaxId, setBillingTaxId] = useState('');
    const [submitting, setSubmitting] = useState(false);
    const navigate = useNavigate();
    const [confirmationToken, setConfirmationToken] = useState<ConfirmationToken | null>(null);

    let name, restAddress;
    if (data) {
        ({name, ...restAddress} = data);
    }

    const handleNext = async () => {
        if (activeStep === 0 && saveAddressChecked && elements) {
            const address = await getStripeAddress();
            if (address) await updateAddress(address);
        }
        if (activeStep === 1) {
            if (!elements || !stripe) return;
            const result = await elements.submit();
            if (result.error) return toast.error(result.error.message);

            const stripeResult = await stripe.createConfirmationToken({elements});
            if (stripeResult.error) return toast.error(stripeResult.error.message);
            setConfirmationToken(stripeResult.confirmationToken);
        }
        if (activeStep === 2) {
            await confirmPayment();
        }
        if (activeStep < 2) setActiveStep(step => step + 1);
    }

    const confirmPayment = async () => {
        setSubmitting(true);
        try {
            if (!confirmationToken || !basket?.clientSecret) 
                throw new Error('Unable to process payment');

            // create order model now but create the order only after payment succeeds
            const orderModel = await createOrderModel();

            const paymentResult = await stripe?.confirmPayment({
                clientSecret: basket.clientSecret,
                redirect: 'if_required',
                confirmParams: {
                    confirmation_token: confirmationToken.id,
                    payment_method_data: {
                        billing_details: {
                            phone
                        }
                    }
                }
            });

            if (paymentResult?.paymentIntent?.status === 'succeeded') {
                const createdOrder = await createOrder(orderModel).unwrap();
                // Carry discount breakdown into the success page.
                // Backend Order.Discount may represent only coupon discount to avoid double-counting.
                navigate('/checkout/success', {state: {data: createdOrder, productDiscount, couponDiscount}});
                clearBasket();
            } else if (paymentResult?.error) {
                throw new Error(paymentResult.error.message);
            } else {
                throw new Error('Something went wrong');
            }
        } catch (error) {
            if (error instanceof Error) {
                toast.error(error.message)
            }
            setActiveStep(step => step - 1);
        } finally {
            setSubmitting(false)
        }
    }

    const createOrderModel = async () => {
        const shippingAddress = await getStripeAddress();
        const pmPreview = confirmationToken?.payment_method_preview;

        if (!shippingAddress || !pmPreview) throw new Error('Problem creating order');

        let paymentSummary;

        if (pmPreview.card) {
            paymentSummary = pmPreview.card;
        } else {
            // For non-card methods (mb_way, multibanco, etc.) create a minimal PaymentSummary
            paymentSummary = {
                last4: 0,
                brand: pmPreview.type || 'unknown',
                exp_month: 0,
                exp_year: 0
            };
        }

        const nif = billingTaxId.trim();

        return {shippingAddress, paymentSummary, billingTaxId: nif.length ? nif : null}
    }

    const getStripeAddress = async () => {
        const addressElement = elements?.getElement('address');
        if (!addressElement) return null;
        const {value: {name, address}} = await addressElement.getValue();

        if (name && address) return {...address, name}

        return null;
    }    

    const handleBack = () => {
        setActiveStep(step => step - 1);
    }

    const handleAddressChange = (event: StripeAddressElementChangeEvent) => {
        setAddressComplete(event.complete)
    }

    const handlePaymentChange = (event: StripePaymentElementChangeEvent) => {
        setPaymentComplete(event.complete)
    }

    if (isLoading) return <Typography variant="h6">A Carregar...</Typography>

    return (
        <Paper sx={{p: { xs: 2, sm: 3 }, borderRadius: 3, boxSizing: 'border-box' }}>
            <Stepper activeStep={activeStep} orientation={isSmall ? 'vertical' : 'horizontal'} sx={{ overflowX: 'auto' }}>
                {steps.map((label, index) => {
                    return (
                        <Step key={index}>
                            <StepLabel>
                                <Typography variant={isSmall ? 'body2' : 'body1'}>{label}</Typography>
                            </StepLabel>
                        </Step>
                    )
                })}
            </Stepper>

            <Box sx={{mt: 2}}>
                <Box sx={{display: activeStep === 0 ? 'block' : 'none'}}>
                    <AddressElement 
                        options={{
                            mode: 'shipping',
                            defaultValues: {
                                name: name,
                                address: restAddress
                            }
                        }}
                        onChange={handleAddressChange}
                    />
                    <TextField
                        label="Telemóvel"
                        fullWidth
                        value={phone}
                        onChange={e => setPhone(e.target.value)}
                        sx={{ mt: 2 }}
                    />
                    <TextField
                        label="NIF (opcional)"
                        fullWidth
                        value={billingTaxId}
                        onChange={e => setBillingTaxId(e.target.value)}
                        helperText="Opcional — para emissão de fatura com NIF"
                        inputProps={{ inputMode: 'numeric', maxLength: 20 }}
                        sx={{ mt: 2 }}
                    />
                    <FormControlLabel 
                        sx={{display: 'flex', justifyContent: 'end'}}
                        control={<Checkbox 
                            checked={saveAddressChecked}
                            onChange={e => setSaveAddressChecked(e.target.checked)}
                        />}
                        label='Gravar para a próxima vez'
                    />
                </Box>
                <Box sx={{display: activeStep === 1 ? 'block' : 'none'}}>
                    <PaymentElement onChange={handlePaymentChange} />
                </Box>
                <Box sx={{display: activeStep === 2 ? 'block' : 'none'}}>
                    <Review confirmationToken={confirmationToken} />
                </Box>
            </Box>

            <Box sx={{ display: 'flex', pt: 2, justifyContent: 'space-between', flexDirection: { xs: 'column', sm: 'row' }, gap: 1 }}>
                <Button
                    onClick={handleBack}
                    variant="outlined"
                    sx={{ width: { xs: '100%', sm: 'auto' }, ...secondaryActionSx(theme) }}
                >
                    Voltar
                </Button>
                <LoadingButton 
                    onClick={handleNext}
                    disabled={
                        (activeStep === 0 && !addressComplete) ||
                        (activeStep === 1 && !paymentComplete) ||
                        submitting
                    }
                    loading={submitting}
                    variant="contained"
                    sx={{ width: { xs: '100%', sm: 'auto' }, ...primaryActionSx(theme) }}
                >
                    {activeStep === steps.length - 1 ? `Pagar ${currencyFormat(total)}` : 'Seguinte'}
                </LoadingButton>
            </Box>
        </Paper>
    )
}